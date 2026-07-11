using Synth.Core;

namespace Synth.Api.Search;

/// <summary>
/// Flat, serializable projection of a single <see cref="CodeChunk"/> returned by the
/// <c>GET /repositories/{collection}/files/{*relativePath}</c> browse endpoint. Unlike
/// <see cref="Mcp.CodeSearchResult"/> — which carries a rerank score and only the raw snippet —
/// this exposes the assembled <see cref="CodeChunk.EmbeddingText"/> alongside the raw
/// <see cref="CodeChunk.Content"/>, since the whole point of browsing a file is inspecting exactly
/// what text was fed to the embedding model for each chunk, not just re-reading the source.
/// </summary>
public sealed record FileChunkResult(
    ChunkType ChunkType,
    string? ClassName,
    string? MethodName,
    string QualifiedName,
    int StartLine,
    int EndLine,
    string Content,
    string? Summary,
    string EmbeddingText)
{
    /// <summary>Projects a stored <see cref="CodeChunk"/> into a browse result.</summary>
    public static FileChunkResult From(CodeChunk chunk) => new(
        chunk.ChunkType,
        string.IsNullOrEmpty(chunk.ClassName) ? null : chunk.ClassName,
        string.IsNullOrEmpty(chunk.MethodName) ? null : chunk.MethodName,
        chunk.QualifiedName,
        chunk.StartLine,
        chunk.EndLine,
        chunk.Content,
        string.IsNullOrEmpty(chunk.Summary) ? null : chunk.Summary,
        chunk.EmbeddingText);
}
