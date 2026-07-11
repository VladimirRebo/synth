using Synth.Core;
using Synth.Domain;

namespace Synth.Api.Mcp;

/// <summary>
/// Flat, serializable projection of a <see cref="CodeChunk"/> returned by the <c>get_symbol</c> MCP
/// tool. A close sibling of <see cref="CodeSearchResult"/> — same locate-and-read fields (file path,
/// class/method name, snippet) — but with no <c>Score</c>: <c>get_symbol</c> does an exact
/// class/method lookup and never runs vector search, so there is no similarity score to report.
/// </summary>
public sealed record SymbolResult(
    string RelativePath,
    string? ClassName,
    string? MethodName,
    string QualifiedName,
    ChunkType ChunkType,
    int StartLine,
    int EndLine,
    string Snippet,
    string? SourceUrl)
{
    /// <summary>Projects a matched <see cref="CodeChunk"/> into a tool result.</summary>
    public static SymbolResult From(CodeChunk chunk) => new(
        chunk.RelativePath,
        string.IsNullOrEmpty(chunk.ClassName) ? null : chunk.ClassName,
        string.IsNullOrEmpty(chunk.MethodName) ? null : chunk.MethodName,
        chunk.QualifiedName,
        chunk.ChunkType,
        chunk.StartLine,
        chunk.EndLine,
        chunk.Content,
        chunk.SourceUrl);
}
