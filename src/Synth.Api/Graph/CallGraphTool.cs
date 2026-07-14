using System.ComponentModel;
using ModelContextProtocol.Server;
using Synth.Domain.Graph;
using Synth.Domain;

namespace Synth.Api.Graph;

/// <summary>
/// Transport-agnostic MCP tools that expose Synth's structural call graph to MCP clients — the
/// precise "who calls X / what does X call" queries that vector search can only approximate
/// (issue #33). Mirrors <see cref="Synth.Api.Mcp.CodeSearchTool"/>'s shape: the wrapping
/// (symbol/collection in, <see cref="CallEdge"/>s out) lives here, the actual lookup stays in
/// <see cref="ICodeGraphStore"/> (SYNTH-25). Registered via
/// <c>AddMcpServer().WithTools&lt;CallGraphTool&gt;()</c> and served over both the HTTP and stdio
/// transports the same way <c>search_code</c> already is.
/// </summary>
[McpServerToolType]
public sealed class CallGraphTool
{
    /// <summary>
    /// Edges where <paramref name="symbol"/> is the callee — the callers of the symbol.
    /// <paramref name="store"/> is injected from DI per invocation; the rest come from the MCP
    /// request arguments.
    /// </summary>
    [McpServerTool(Name = "find_callers")]
    [Description(
        "Find the call sites that call a given symbol (its callers) using Synth's structural call " +
        "graph — exact, unlike vector search. Returns one edge per call site (caller, callee, " +
        "source file and line).")]
    public static async Task<IReadOnlyList<CallEdge>> FindCallersAsync(
        ICodeGraphStore store,
        [Description(
            "Qualified name of the symbol whose callers to find, in the extractor's " +
            "Namespace.ClassName.MethodName form (e.g. \"Synth.Application.CodeSearchService.SearchAsync\").")]
        string symbol,
        [Description(
            "Name of the indexed collection (repository) to search. Leave unset to search the " +
            "main indexed codebase.")]
        string collection = CollectionNames.Default,
        CancellationToken cancellationToken = default)
    {
        var target = string.IsNullOrWhiteSpace(collection) ? CollectionNames.Default : collection;
        return await store.FindCallersAsync(target, symbol, cancellationToken);
    }

    /// <summary>
    /// Edges where <paramref name="symbol"/> is the caller — what the symbol calls.
    /// <paramref name="store"/> is injected from DI per invocation; the rest come from the MCP
    /// request arguments.
    /// </summary>
    [McpServerTool(Name = "find_callees")]
    [Description(
        "Find the symbols a given symbol calls (its callees) using Synth's structural call graph — " +
        "exact, unlike vector search. Returns one edge per call site (caller, callee, source file " +
        "and line).")]
    public static async Task<IReadOnlyList<CallEdge>> FindCalleesAsync(
        ICodeGraphStore store,
        [Description(
            "Qualified name of the symbol whose callees to find, in the extractor's " +
            "Namespace.ClassName.MethodName form (e.g. \"Synth.Application.CodeSearchService.SearchAsync\").")]
        string symbol,
        [Description(
            "Name of the indexed collection (repository) to search. Leave unset to search the " +
            "main indexed codebase.")]
        string collection = CollectionNames.Default,
        CancellationToken cancellationToken = default)
    {
        var target = string.IsNullOrWhiteSpace(collection) ? CollectionNames.Default : collection;
        return await store.FindCalleesAsync(target, symbol, cancellationToken);
    }
}
