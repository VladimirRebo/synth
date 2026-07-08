using System.ComponentModel;
using ModelContextProtocol.Server;
using Synth.Core;

namespace Synth.Api.Mcp;

/// <summary>
/// Transport-agnostic MCP tool definition that exposes Synth's code search to MCP clients.
/// The wrapping (query/limit in, projected <see cref="CodeSearchResult"/>s out) lives here; the
/// actual retrieval + rerank stays in <see cref="CodeSearchService"/> from SYNTH-11. Registered
/// via <c>AddMcpServer().WithTools&lt;CodeSearchTool&gt;()</c> and served over HTTP in Program.cs;
/// the same type can back a stdio host in a later task without changes.
/// </summary>
[McpServerToolType]
public sealed class CodeSearchTool
{
    /// <summary>
    /// The single MCP tool: searches the indexed codebase and returns the most relevant chunks.
    /// <paramref name="searchService"/> is injected from DI per invocation; <paramref name="query"/>
    /// and <paramref name="limit"/> come from the MCP request arguments.
    /// </summary>
    [McpServerTool(Name = "search_code")]
    [Description(
        "Search the indexed codebase for the code most relevant to a natural-language or " +
        "keyword query. Returns each hit's file path, class/method name and a source snippet.")]
    public static async Task<IReadOnlyList<CodeSearchResult>> SearchCodeAsync(
        CodeSearchService searchService,
        [Description("Natural-language or keyword description of the code to find.")]
        string query,
        [Description("Maximum number of results to return. Defaults to 5.")]
        int limit = 5,
        [Description(
            "Name of the indexed collection (repository) to search. Leave unset to search the " +
            "main indexed codebase.")]
        string? collection = null,
        CancellationToken cancellationToken = default)
    {
        var target = string.IsNullOrWhiteSpace(collection) ? CollectionNames.Default : collection;
        var chunks = await searchService.SearchAsync(target, query, limit, cancellationToken);
        return chunks.Select(CodeSearchResult.From).ToList();
    }
}
