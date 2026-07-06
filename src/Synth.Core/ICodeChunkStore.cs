namespace Synth.Core;

/// <summary>
/// Vector-store abstraction over <see cref="CodeChunk"/>: upsert embedded chunks,
/// nearest-neighbour search by query vector, and retrieval of all chunks for a file.
/// Deliberately minimal infrastructure plumbing — reranking/query-expansion live in a
/// later task. Mirrors Sonar's Qdrant/Milvus/Local store split.
/// </summary>
public interface ICodeChunkStore
{
    /// <summary>
    /// Inserts or updates the given chunks, keyed by <see cref="CodeChunk.ChunkId"/>.
    /// Each chunk is expected to carry a populated <see cref="CodeChunk.Embedding"/>.
    /// </summary>
    Task UpsertAsync(IEnumerable<CodeChunk> chunks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="limit"/> chunks whose embedding is most similar to
    /// <paramref name="queryVector"/> (cosine), ordered by descending similarity score.
    /// </summary>
    Task<IReadOnlyList<(CodeChunk Chunk, float Score)>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every stored chunk whose <see cref="CodeChunk.RelativePath"/> matches
    /// <paramref name="relativePath"/>, in ascending <see cref="CodeChunk.StartLine"/> order.
    /// </summary>
    Task<IReadOnlyList<CodeChunk>> GetByFileAsync(
        string relativePath,
        CancellationToken cancellationToken = default);
}
