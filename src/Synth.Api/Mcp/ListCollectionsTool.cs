using System.ComponentModel;
using ModelContextProtocol.Server;
using Synth.Api.Vcs;

namespace Synth.Api.Mcp;

/// <summary>
/// Transport-agnostic MCP tool that lists the indexed collections and their metadata — the MCP
/// equivalent of <c>GET /repositories</c> (<see cref="RepositoryEndpoints"/>, part of issue #44).
/// A thin wrapper: the actual data comes straight from <see cref="IRepositoryRegistry.ListAsync"/>,
/// the same source the REST endpoint reads, returning <see cref="RepositoryEntry"/> records directly
/// (already a clean serializable shape). Registered via
/// <c>AddMcpServer().WithTools&lt;ListCollectionsTool&gt;()</c> over both the HTTP and stdio transports.
/// </summary>
[McpServerToolType]
public sealed class ListCollectionsTool
{
    /// <summary>
    /// Returns every known collection and its metadata. <paramref name="registry"/> is injected from
    /// DI per invocation, exactly as every other tool in this directory takes its dependencies.
    /// </summary>
    [McpServerTool(Name = "list_collections")]
    [Description(
        "List the indexed collections (repositories) and their metadata — collection name, source " +
        "type/URL, indexed branch, last-indexed time and chunk count. Use this to discover valid " +
        "collection names to pass to search_code, get_symbol, get_file or delete_collection.")]
    public static async Task<IReadOnlyList<RepositoryEntry>> ListCollectionsAsync(
        IRepositoryRegistry registry,
        CancellationToken cancellationToken = default) =>
        await registry.ListAsync(cancellationToken);
}
