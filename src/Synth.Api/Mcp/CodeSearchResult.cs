using Synth.Core;

namespace Synth.Api.Mcp;

/// <summary>
/// Flat, serializable projection of a <see cref="CodeChunk"/> returned by the
/// <c>search_code</c> MCP tool. Carries just what an MCP client needs to locate and
/// read a hit — file path, class/method name and a source snippet — instead of the full
/// internal chunk (embedding vectors, file hashes, etc.).
/// </summary>
public sealed record CodeSearchResult(
    string RelativePath,
    string? ClassName,
    string? MethodName,
    string QualifiedName,
    ChunkType ChunkType,
    int StartLine,
    int EndLine,
    string Snippet)
{
    /// <summary>Projects a matched <see cref="CodeChunk"/> into a tool result.</summary>
    public static CodeSearchResult From(CodeChunk chunk) => new(
        chunk.RelativePath,
        string.IsNullOrEmpty(chunk.ClassName) ? null : chunk.ClassName,
        string.IsNullOrEmpty(chunk.MethodName) ? null : chunk.MethodName,
        chunk.QualifiedName,
        chunk.ChunkType,
        chunk.StartLine,
        chunk.EndLine,
        chunk.Content);
}
