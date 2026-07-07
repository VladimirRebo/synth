using Synth.Core;

namespace Synth.Api.Mcp;

/// <summary>
/// Flat, serializable projection of a <see cref="ScoredCodeChunk"/> returned by the
/// <c>search_code</c> MCP tool (and the <c>GET /search</c> REST endpoint, which shares this
/// same shape). Carries just what a client needs to locate and read a hit — file path,
/// class/method name, a source snippet and its rerank score — instead of the full internal
/// chunk (embedding vectors, file hashes, etc.).
/// </summary>
public sealed record CodeSearchResult(
    string RelativePath,
    string? ClassName,
    string? MethodName,
    string QualifiedName,
    ChunkType ChunkType,
    int StartLine,
    int EndLine,
    string Snippet,
    double Score)
{
    /// <summary>Projects a matched <see cref="ScoredCodeChunk"/> into a tool result.</summary>
    public static CodeSearchResult From(ScoredCodeChunk scored)
    {
        var chunk = scored.Chunk;
        return new CodeSearchResult(
            chunk.RelativePath,
            string.IsNullOrEmpty(chunk.ClassName) ? null : chunk.ClassName,
            string.IsNullOrEmpty(chunk.MethodName) ? null : chunk.MethodName,
            chunk.QualifiedName,
            chunk.ChunkType,
            chunk.StartLine,
            chunk.EndLine,
            chunk.Content,
            scored.Score);
    }
}
