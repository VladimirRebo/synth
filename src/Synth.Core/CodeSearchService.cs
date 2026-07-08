using Microsoft.Extensions.AI;

namespace Synth.Core;

/// <summary>
/// A <see cref="CodeChunk"/> paired with its final rerank score from
/// <see cref="CodeSearchService.SearchAsync"/>. Not bounded to [0, 1] — it is raw cosine
/// similarity multiplied by chunk-type weight (up to 1.15) and keyword boost (1 + 0.5 per
/// matching token), so it can exceed 1. Treat it as a relative ranking signal, not a percentage.
/// </summary>
public readonly record struct ScoredCodeChunk(CodeChunk Chunk, double Score);

/// <summary>
/// Search entry point over the indexed <see cref="CodeChunk"/>s: expands the query, embeds it,
/// over-fetches candidates from the vector store and reranks them with a lightweight scoring
/// model (vector similarity × chunk-type weight × keyword boost) before deduplicating and
/// truncating to the requested limit. Ported from Sonar's search pipeline — this is the
/// "better-than-raw-cosine" ranking layer that sits on top of <see cref="ICodeChunkStore"/>.
/// </summary>
public sealed class CodeSearchService
{
    /// <summary>How many candidates to pull per requested hit before reranking/dedup trims back.</summary>
    private const int OverFetchFactor = 4;

    /// <summary>Additional boost per distinct query token that matches the chunk's class/method name.</summary>
    private const double KeywordBoostPerMatch = 0.5;

    /// <summary>At most this many surviving hits may share the same method name.</summary>
    private const int MaxHitsPerMethodName = 2;

    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ICodeChunkStore _store;
    private readonly QueryExpander _queryExpander;

    public CodeSearchService(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ICodeChunkStore store,
        QueryExpander queryExpander)
    {
        _embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _queryExpander = queryExpander ?? throw new ArgumentNullException(nameof(queryExpander));
    }

    /// <summary>
    /// Returns up to <paramref name="limit"/> code chunks in <paramref name="collection"/> most
    /// relevant to <paramref name="query"/>, each paired with its rerank score, ordered by
    /// descending score. Returns an empty list for a non-positive limit or a blank query.
    /// </summary>
    public async Task<IReadOnlyList<ScoredCodeChunk>> SearchAsync(
        string collection,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || string.IsNullOrWhiteSpace(query))
            return [];

        var expanded = _queryExpander.Expand(query);
        var embedding = await _embeddingGenerator.GenerateAsync([expanded], cancellationToken: cancellationToken);
        var queryVector = embedding[0].Vector;

        // Over-fetch so reranking has room to reorder and dedup can drop near-duplicates
        // without starving the final result set.
        var candidates = await _store.SearchAsync(collection, queryVector, limit * OverFetchFactor, cancellationToken);

        var ranked = candidates
            .Select(candidate => new ScoredCodeChunk(
                candidate.Chunk,
                candidate.Score * ChunkTypeWeight(candidate.Chunk.ChunkType) * KeywordBoost(query, candidate.Chunk)))
            .OrderByDescending(scored => scored.Score);

        return Deduplicate(ranked).Take(limit).ToList();
    }

    // Relative weight per chunk kind: whole type declarations rank above members, member
    // bodies and prose below signatures — so an otherwise-equal vector score prefers the
    // more self-contained, name-bearing construct. Weights are intentionally distinct.
    internal static double ChunkTypeWeight(ChunkType chunkType) => chunkType switch
    {
        ChunkType.Class or ChunkType.Interface or ChunkType.Record or ChunkType.Struct => 1.15,
        ChunkType.Method or ChunkType.Constructor => 1.10,
        ChunkType.MethodHead => 1.05,
        ChunkType.Property => 0.95,
        ChunkType.MethodBody => 0.90,
        ChunkType.Markdown => 0.85,
        _ => 0.90,
    };

    // Multiplier ≥ 1 that grows with how many distinct query tokens appear among the chunk's
    // class + method name tokens, using camelCase-aware tokenization (so "user" matches
    // GetUserById). No overlap => 1.0 (score unchanged). Uses the raw query, not the expanded one.
    internal static double KeywordBoost(string query, CodeChunk chunk)
    {
        var queryTokens = new HashSet<string>(IdentifierTokenizer.Tokenize(query));
        if (queryTokens.Count == 0)
            return 1.0;

        var nameTokens = new HashSet<string>(
            IdentifierTokenizer.Tokenize($"{chunk.ClassName} {chunk.MethodName}"));

        var matches = queryTokens.Count(nameTokens.Contains);
        return 1.0 + KeywordBoostPerMatch * matches;
    }

    // Dedup pass over already score-sorted chunks: at most one hit per
    // RelativePath::ClassName.MethodName, and at most two hits sharing a method name
    // (the method-name cap only applies to chunks that actually carry a method name, so
    // type-level chunks aren't collapsed against each other).
    private static IEnumerable<ScoredCodeChunk> Deduplicate(IEnumerable<ScoredCodeChunk> scored)
    {
        var seenExact = new HashSet<string>(StringComparer.Ordinal);
        var perMethodName = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in scored)
        {
            var chunk = entry.Chunk;
            var exactKey = $"{chunk.RelativePath}::{chunk.ClassName}.{chunk.MethodName}";
            if (!seenExact.Add(exactKey))
                continue;

            if (!string.IsNullOrEmpty(chunk.MethodName))
            {
                perMethodName.TryGetValue(chunk.MethodName, out var count);
                if (count >= MaxHitsPerMethodName)
                    continue;
                perMethodName[chunk.MethodName] = count + 1;
            }

            yield return entry;
        }
    }
}
