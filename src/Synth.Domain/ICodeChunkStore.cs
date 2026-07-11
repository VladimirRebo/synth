namespace Synth.Domain;

/// <summary>
/// Vector-store abstraction over <see cref="CodeChunk"/>: upsert embedded chunks,
/// nearest-neighbour search by query vector, and retrieval of all chunks for a file.
/// Deliberately minimal infrastructure plumbing — reranking/query-expansion live in a
/// later task. Mirrors Sonar's Qdrant/Milvus/Local store split.
///
/// Every operation is scoped to a named <c>collection</c> (see <see cref="CollectionNames"/>):
/// chunks upserted/searched/fetched under one collection are never visible from another, so
/// multiple repositories can be indexed side by side without their chunks colliding.
/// </summary>
public interface ICodeChunkStore
{
    /// <summary>
    /// Inserts or updates the given chunks in <paramref name="collection"/>, keyed by
    /// <see cref="CodeChunk.ChunkId"/>. Each chunk is expected to carry a populated
    /// <see cref="CodeChunk.Embedding"/>.
    /// </summary>
    Task UpsertAsync(string collection, IEnumerable<CodeChunk> chunks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="limit"/> chunks in <paramref name="collection"/> whose
    /// embedding is most similar to <paramref name="queryVector"/> (cosine), ordered by
    /// descending similarity score.
    /// </summary>
    Task<IReadOnlyList<(CodeChunk Chunk, float Score)>> SearchAsync(
        string collection,
        ReadOnlyMemory<float> queryVector,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every chunk in <paramref name="collection"/> whose <see cref="CodeChunk.RelativePath"/>
    /// matches <paramref name="relativePath"/>, in ascending <see cref="CodeChunk.StartLine"/> order.
    /// </summary>
    Task<IReadOnlyList<CodeChunk>> GetByFileAsync(
        string collection,
        string relativePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every chunk in <paramref name="collection"/> whose <see cref="CodeChunk.ClassName"/>
    /// and/or <see cref="CodeChunk.MethodName"/> exactly match (case-insensitively) the given
    /// <paramref name="className"/>/<paramref name="methodName"/>. A null (or empty) filter argument
    /// is ignored, so passing only <paramref name="className"/> returns every chunk of that class and
    /// passing both returns only chunks matching both (AND). Callers must supply at least one of the
    /// two — the store does not enforce that; the MCP tool layer does. Mirrors the
    /// <c>Namespace.ClassName.MethodName</c> naming <c>find_callers</c>/<c>find_callees</c> key on.
    /// </summary>
    Task<IReadOnlyList<CodeChunk>> GetBySymbolAsync(
        string collection,
        string? className,
        string? methodName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every distinct <see cref="CodeChunk.RelativePath"/> currently stored in
    /// <paramref name="collection"/>. Used by incremental indexing to detect files that were
    /// indexed on a previous run but no longer exist on disk. An unknown collection yields an
    /// empty set rather than throwing.
    /// </summary>
    Task<IReadOnlyList<string>> ListRelativePathsAsync(
        string collection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every chunk in <paramref name="collection"/> whose <see cref="CodeChunk.RelativePath"/>
    /// matches <paramref name="relativePath"/>. A no-op when the collection or file is absent.
    /// </summary>
    Task DeleteByFileAsync(
        string collection,
        string relativePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the entire <paramref name="collection"/> and all chunks it holds. A no-op when the
    /// collection does not exist, so deleting an unknown collection is clean rather than an error.
    /// Used when a repository is removed from Synth (<c>DELETE /repositories/{collection}</c>).
    /// </summary>
    Task DeleteCollectionAsync(
        string collection,
        CancellationToken cancellationToken = default);
}
