using System.ComponentModel;
using ModelContextProtocol.Server;
using Synth.Core;
using Synth.Domain;

namespace Synth.Api.Mcp;

/// <summary>
/// Transport-agnostic MCP tool for exact class/method lookup in the indexed codebase (part of issue
/// #44). Unlike <see cref="CodeSearchTool"/>'s fuzzy vector search, this makes no embedding call — it
/// filters the vector store's chunks by <see cref="CodeChunk.ClassName"/>/<see cref="CodeChunk.MethodName"/>
/// directly, so it is cheap and precise when an agent already knows the name it wants. Mirrors
/// <see cref="CodeSearchTool"/>'s shape (the wrapping lives here; retrieval stays in
/// <see cref="ICodeChunkStore.GetBySymbolAsync"/>). Registered via
/// <c>AddMcpServer().WithTools&lt;GetSymbolTool&gt;()</c> over both the HTTP and stdio transports.
/// </summary>
[McpServerToolType]
public sealed class GetSymbolTool
{
    /// <summary>
    /// Looks up chunks by exact class/method name. <paramref name="store"/> is injected from DI per
    /// invocation; the rest come from the MCP request arguments. At least one of
    /// <paramref name="className"/>/<paramref name="methodName"/> must be provided.
    /// </summary>
    [McpServerTool(Name = "get_symbol")]
    [Description(
        "Look up a class or method by its exact name (case-insensitive) in the indexed codebase — a " +
        "cheap, precise alternative to search_code when you already know the name. Makes no embedding " +
        "call. Provide at least one of 'className'/'methodName'; giving both narrows to chunks " +
        "matching both. Returns each match's file path, class/method name and source snippet.")]
    public static async Task<IReadOnlyList<SymbolResult>> GetSymbolAsync(
        ICodeChunkStore store,
        [Description(
            "Exact class (or interface) name to match, case-insensitive. Provide this and/or " +
            "'methodName' — at least one is required.")]
        string? className = null,
        [Description(
            "Exact method (or member) name to match, case-insensitive. Provide this and/or " +
            "'className' — at least one is required.")]
        string? methodName = null,
        [Description(
            "Name of the indexed collection (repository) to search. Leave unset to search the " +
            "main indexed codebase.")]
        string? collection = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(className) && string.IsNullOrWhiteSpace(methodName))
            throw new ArgumentException("Provide at least one of 'className' or 'methodName'.");

        var target = string.IsNullOrWhiteSpace(collection) ? CollectionNames.Default : collection;
        var chunks = await store.GetBySymbolAsync(
            target,
            string.IsNullOrWhiteSpace(className) ? null : className,
            string.IsNullOrWhiteSpace(methodName) ? null : methodName,
            cancellationToken);

        return chunks.Select(SymbolResult.From).ToList();
    }
}
