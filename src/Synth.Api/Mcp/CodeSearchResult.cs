using Synth.Application;
using Synth.Domain;

namespace Synth.Api.Mcp;

/// <summary>
/// Flat, serializable projection of a <see cref="ScoredCodeChunk"/> returned by the
/// <c>search_code</c> MCP tool (and the <c>GET /search</c> REST endpoint, which shares this
/// same shape). Carries just what a client needs to locate and read a hit — file path,
/// class/method name, a source snippet and its rerank score — instead of the full internal
/// chunk (embedding vectors, file hashes, etc.).
/// </summary>
/// <param name="Collection">
/// Which collection this hit was found in. Populated only for all-collections search (SYNTH-48), so
/// the client can label "found in collection X"; <c>null</c> for a single-collection search, whose
/// caller already knows the collection and doesn't want the extra visual noise.
/// </param>
public sealed record CodeSearchResult(
    string RelativePath,
    string? ClassName,
    string? MethodName,
    string QualifiedName,
    ChunkType ChunkType,
    int StartLine,
    int EndLine,
    string Snippet,
    double Score,
    string? SourceUrl,
    string? Collection)
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
            scored.Score,
            chunk.SourceUrl,
            string.IsNullOrEmpty(scored.Collection) ? null : scored.Collection);
    }
}
