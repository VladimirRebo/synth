using Microsoft.Extensions.AI;

namespace Synth.Core;

/// <summary>
/// Ties chunking, embedding and storage together for a directory of source files:
/// walk the tree, chunk each supported file with the first matching
/// <see cref="IFileChunker"/>, embed every chunk's <see cref="CodeChunk.EmbeddingText"/>
/// via the registered <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>, and upsert
/// the embedded chunks into <see cref="ICodeChunkStore"/>. Mirrors Sonar's
/// <c>IndexingPipeline</c>, simplified. Search/reranking live in a later task.
/// </summary>
public sealed class IndexingPipeline
{
    /// <summary>Directory names never descended into (build output and VCS metadata).</summary>
    private static readonly string[] SkippedDirectorySegments = ["bin", "obj", ".git"];

    private readonly IReadOnlyList<IFileChunker> _chunkers;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ICodeChunkStore _store;

    public IndexingPipeline(
        IEnumerable<IFileChunker> chunkers,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ICodeChunkStore store)
    {
        ArgumentNullException.ThrowIfNull(chunkers);
        _chunkers = chunkers.ToList();
        _embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Indexes every supported file under <paramref name="rootPath"/> (recursively,
    /// skipping <c>bin/</c>, <c>obj/</c> and <c>.git/</c>). Files that no chunker
    /// handles, are empty, or cannot be read are skipped rather than aborting the run.
    /// </summary>
    /// <returns>A summary of how many files were indexed vs. skipped and the chunk total.</returns>
    public async Task<IndexingSummary> IndexDirectoryAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"Index root not found: {rootPath}");

        var filesIndexed = 0;
        var filesSkipped = 0;
        var chunksIndexed = 0;

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
            await _store.UpsertAsync(embedded, cancellationToken);

            filesIndexed++;
            chunksIndexed += embedded.Count;
        }

        return new IndexingSummary(filesIndexed, filesSkipped, chunksIndexed);
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

    private IEnumerable<string> EnumerateSourceFiles(string rootPath) =>
        // Start with C# only — matching the one chunker that exists (CSharpRoslynChunker).
        Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsInSkippedDirectory(rootPath, path));

    private static bool IsInSkippedDirectory(string rootPath, string filePath)
    {
        var relative = Path.GetRelativePath(rootPath, filePath);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        // Inspect directory segments only (drop the trailing file name).
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (SkippedDirectorySegments.Contains(segments[i], StringComparer.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
