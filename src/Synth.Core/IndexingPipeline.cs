using Microsoft.Extensions.AI;
using Synth.Core.Graph;

namespace Synth.Core;

/// <summary>
/// Ties chunking, embedding and storage together for a directory of source files:
/// walk the tree, chunk each supported file with the first matching
/// <see cref="IFileChunker"/>, embed every chunk's <see cref="CodeChunk.EmbeddingText"/>
/// via the registered <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>, and upsert
/// the embedded chunks into <see cref="ICodeChunkStore"/>. Mirrors Sonar's
/// <c>IndexingPipeline</c>, simplified. Search/reranking live in a later task.
/// Chunkers that also implement <see cref="ICallSiteExtractor"/> additionally feed a syntax-heuristic
/// call graph into <see cref="ICodeGraphStore"/>, resolved collection-wide at the end of a run.
/// </summary>
public sealed class IndexingPipeline
{
    /// <summary>Directory names never descended into (build output and VCS metadata).</summary>
    private static readonly string[] SkippedDirectorySegments = ["bin", "obj", ".git"];

    private readonly IReadOnlyList<IFileChunker> _chunkers;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ICodeChunkStore _store;
    private readonly ICodeGraphStore _graphStore;

    public IndexingPipeline(
        IEnumerable<IFileChunker> chunkers,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ICodeChunkStore store,
        ICodeGraphStore graphStore)
    {
        ArgumentNullException.ThrowIfNull(chunkers);
        _chunkers = chunkers.ToList();
        _embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
    }

    /// <summary>
    /// Indexes every supported file under <paramref name="rootPath"/> (recursively,
    /// skipping <c>bin/</c>, <c>obj/</c> and <c>.git/</c>) into <paramref name="collection"/>.
    /// Files that no chunker handles, are empty, or cannot be read are skipped rather than
    /// aborting the run.
    /// </summary>
    /// <param name="progress">
    /// Optional, additive progress sink (issue #39): reported once at the start with the upfront
    /// total-file count, then after each indexed file, and once more with the final counts. Callers
    /// that omit it (tests, existing callers) are unaffected — behavior is otherwise identical.
    /// </param>
    /// <returns>A summary of how many files were indexed vs. skipped and the chunk total.</returns>
    public async Task<IndexingSummary> IndexDirectoryAsync(
        string collection,
        string rootPath,
        CancellationToken cancellationToken = default,
        IProgress<IndexingProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"Index root not found: {rootPath}");

        var filesIndexed = 0;
        var filesSkipped = 0;
        var chunksIndexed = 0;

        // Count matching files upfront so progress carries a denominator. EnumerateSourceFiles is a
        // lazy directory walk, so this .Count() is a plain filesystem pass — cheap next to embedding.
        var totalFiles = EnumerateSourceFiles(rootPath).Count();
        progress?.Report(new IndexingProgress(filesIndexed, filesSkipped, totalFiles));

        // Call-graph accumulators (stage 1). Only lightweight strings are held across files — never the
        // full CodeChunk content — so this stays within the existing per-file streaming design.
        var rawCallSites = new List<RawCallSite>();
        var knownSymbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (var filePath in EnumerateSourceFiles(rootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunker = _chunkers.FirstOrDefault(c => c.CanHandle(filePath));
            if (chunker is null)
            {
                filesSkipped++;
                continue;
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(filePath, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable file: skip it, don't take down the whole run.
                filesSkipped++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                filesSkipped++;
                continue;
            }

            var relativePath = NormalizeRelativePath(Path.GetRelativePath(rootPath, filePath));
            var chunks = chunker.Chunk(filePath, relativePath, content);
            if (chunks.Count == 0)
            {
                filesSkipped++;
                continue;
            }

            var embedded = await EmbedAsync(chunks, cancellationToken);
            await _store.UpsertAsync(collection, embedded, cancellationToken);

            // Stage 1: remember this file's declared method/constructor names (candidate callees) and,
            // if the chunker supports it, its raw call sites — for collection-wide resolution below.
            foreach (var chunk in chunks)
            {
                if (IsCallableMember(chunk.ChunkType) && chunk.QualifiedName.Length > 0)
                    knownSymbols.Add(chunk.QualifiedName);
            }

            if (chunker is ICallSiteExtractor extractor)
                rawCallSites.AddRange(extractor.ExtractCallSites(filePath, relativePath, content));

            filesIndexed++;
            chunksIndexed += embedded.Count;

            progress?.Report(new IndexingProgress(filesIndexed, filesSkipped, totalFiles));
        }

        // Stage 2: resolve every raw call site against the whole collection's known method names and
        // replace the graph in one shot. An empty edge set (no .cs files, or no resolved calls) still
        // replaces — clearing any stale edges from a previous index run.
        var edges = ResolveEdges(collection, rawCallSites, knownSymbols);
        await _graphStore.ReplaceEdgesAsync(collection, edges, cancellationToken);

        // Final report guarantees the last-seen counts match the summary even when the run ended on a
        // string of skipped files (which don't emit a per-file report of their own).
        progress?.Report(new IndexingProgress(filesIndexed, filesSkipped, totalFiles));

        return new IndexingSummary(filesIndexed, filesSkipped, chunksIndexed);
    }

    // Chunk kinds that represent a callable member (a candidate callee). Class/interface/property/etc.
    // chunks are never call targets. MethodHead/MethodBody are two halves of one long method, so they
    // fold to the same qualified name — the HashSet in stage 1 dedups them.
    private static bool IsCallableMember(ChunkType chunkType) =>
        chunkType is ChunkType.Method or ChunkType.Constructor or ChunkType.MethodHead or ChunkType.MethodBody;

    // Resolves raw call sites into edges by matching each invoked simple name against every known
    // qualified name sharing that last segment. One invoked name matching several methods emits one
    // edge per match (approximate, by design — issue #33); a name matching nothing emits no edge.
    private static IReadOnlyList<CallEdge> ResolveEdges(
        string collection,
        List<RawCallSite> callSites,
        HashSet<string> knownSymbols)
    {
        var bySimpleName = knownSymbols
            .GroupBy(SimpleNameOf, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var edges = new List<CallEdge>();
        foreach (var site in callSites)
        {
            if (!bySimpleName.TryGetValue(site.InvokedName, out var callees))
                continue;

            foreach (var callee in callees)
                edges.Add(new CallEdge(collection, site.CallerQualifiedName, callee, site.SourceFile, site.Line));
        }

        return edges;
    }

    private static string SimpleNameOf(string qualifiedName)
    {
        var lastDot = qualifiedName.LastIndexOf('.');
        return lastDot < 0 ? qualifiedName : qualifiedName[(lastDot + 1)..];
    }

    // Embeds a whole file's chunks in one batched generator call, then attaches each
    // resulting vector to a copy of its chunk (the model is init-only / immutable).
    private async Task<IReadOnlyList<CodeChunk>> EmbedAsync(
        IReadOnlyList<CodeChunk> chunks,
        CancellationToken cancellationToken)
    {
        var texts = chunks.Select(chunk => chunk.EmbeddingText).ToList();
        var embeddings = await _embeddingGenerator.GenerateAsync(texts, cancellationToken: cancellationToken);

        var embedded = new List<CodeChunk>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
            embedded.Add(chunks[i].WithEmbedding(embeddings[i].Vector));

        return embedded;
    }

    // Start with C# only — matching the one chunker that exists (CSharpRoslynChunker). Walks
    // manually (rather than Directory.EnumerateFiles(..., AllDirectories)) so a single unreadable
    // subdirectory is skipped like any other unreadable file, instead of throwing out of the lazy
    // iterator and aborting the whole run.
    private IEnumerable<string> EnumerateSourceFiles(string rootPath)
    {
        foreach (var file in EnumerateFilesRecursive(rootPath))
            yield return file;
    }

    private static IEnumerable<string> EnumerateFilesRecursive(string directory)
    {
        string[] files;
        string[] subdirectories;
        try
        {
            files = Directory.GetFiles(directory, "*.cs");
            subdirectories = Directory.GetDirectories(directory)
                .Where(d => !SkippedDirectorySegments.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Can't read this directory (permissions, broken symlink, etc.) — skip it rather than
            // aborting the whole run, same policy as an individual unreadable file.
            yield break;
        }

        foreach (var file in files)
            yield return file;

        foreach (var subdirectory in subdirectories)
        {
            foreach (var file in EnumerateFilesRecursive(subdirectory))
                yield return file;
        }
    }

    // Use forward slashes so RelativePath / ChunkId are stable across operating systems.
    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('\\', '/');
}

/// <summary>
/// Observability summary of a single <see cref="IndexingPipeline.IndexDirectoryAsync"/> run:
/// how many files produced chunks, how many were skipped (unsupported/empty/unreadable),
/// and the total number of chunks upserted.
/// </summary>
public readonly record struct IndexingSummary(int FilesIndexed, int FilesSkipped, int ChunksIndexed);

/// <summary>
/// A single progress report emitted by <see cref="IndexingPipeline.IndexDirectoryAsync"/> when a
/// caller supplies an <see cref="IProgress{T}"/> sink (issue #39). <paramref name="TotalFiles"/> is
/// the upfront count of matching files (the denominator); <paramref name="FilesIndexed"/> and
/// <paramref name="FilesSkipped"/> are the running tallies.
/// </summary>
public readonly record struct IndexingProgress(int FilesIndexed, int FilesSkipped, int TotalFiles);
