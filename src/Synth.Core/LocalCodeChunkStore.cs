using System.Collections.Concurrent;

namespace Synth.Core;

/// <summary>
/// In-memory <see cref="ICodeChunkStore"/>: a dictionary keyed by
/// <see cref="CodeChunk.ChunkId"/> plus a brute-force cosine-similarity search.
/// This is the store used by tests and by local dev without Docker/Qdrant, mirroring
/// Sonar's <c>Local</c> store. Fine for small corpora; not meant for production scale.
/// </summary>
public sealed class LocalCodeChunkStore : ICodeChunkStore
{
    private readonly ConcurrentDictionary<string, CodeChunk> _chunks = new();

    public Task UpsertAsync(IEnumerable<CodeChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        foreach (var chunk in chunks)
            _chunks[chunk.ChunkId] = chunk;

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<(CodeChunk Chunk, float Score)>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0 || queryVector.Length == 0)
            return Task.FromResult<IReadOnlyList<(CodeChunk, float)>>([]);

        var results = _chunks.Values
            .Where(chunk => chunk.Embedding.Length == queryVector.Length)
            .Select(chunk => (Chunk: chunk, Score: CosineSimilarity(chunk.Embedding, queryVector)))
            .OrderByDescending(match => match.Score)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<(CodeChunk, float)>>(results);
    }

    public Task<IReadOnlyList<CodeChunk>> GetByFileAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var matches = _chunks.Values
            .Where(chunk => string.Equals(chunk.RelativePath, relativePath, StringComparison.Ordinal))
            .OrderBy(chunk => chunk.StartLine)
            .ToList();

        return Task.FromResult<IReadOnlyList<CodeChunk>>(matches);
    }

    // Cosine similarity in [-1, 1]; 0 when either vector has zero magnitude. Assumes the
    // spans have equal length (guaranteed by the caller's length filter).
    private static float CosineSimilarity(ReadOnlyMemory<float> aMemory, ReadOnlyMemory<float> bMemory)
    {
        var a = aMemory.Span;
        var b = bMemory.Span;

        float dot = 0f, magA = 0f, magB = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA == 0f || magB == 0f)
            return 0f;

        return dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB));
    }
}
