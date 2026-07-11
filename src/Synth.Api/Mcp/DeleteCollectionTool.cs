using System.ComponentModel;
using ModelContextProtocol.Server;
using Synth.Api.Vcs;
using Synth.Core;
using Synth.Core.Graph;

namespace Synth.Api.Mcp;

/// <summary>
/// Transport-agnostic MCP tool that removes an indexed collection completely — the MCP equivalent of
/// <c>DELETE /repositories/{collection}</c> (SYNTH-34, part of issue #44). A thin wrapper: it drives
/// the exact same three-step sequence via <see cref="RepositoryEndpoints.DeleteCollectionAsync"/>
/// (vector-store collection, call-graph edges, registry entry) the REST handler shares, so the two
/// transports can never drift. Registered via
/// <c>AddMcpServer().WithTools&lt;DeleteCollectionTool&gt;()</c> over both the HTTP and stdio transports.
/// </summary>
[McpServerToolType]
public sealed class DeleteCollectionTool
{
    /// <summary>
    /// Deletes <paramref name="collection"/>'s chunks, call-graph edges and registry entry. The store
    /// dependencies are injected from DI per invocation; the result mirrors the REST endpoint's
    /// 204/404 split via <see cref="DeleteCollectionResult.Deleted"/>.
    /// </summary>
    [McpServerTool(Name = "delete_collection")]
    [Description(
        "DESTRUCTIVE: permanently remove an indexed collection (repository) from Synth — deletes its " +
        "vector-store collection, its call-graph edges and its registry entry. This cannot be undone; " +
        "the collection must be re-indexed to search it again. Reports deleted=false when no such " +
        "collection existed.")]
    public static async Task<DeleteCollectionResult> DeleteCollectionAsync(
        ICodeChunkStore chunkStore,
        ICodeGraphStore graphStore,
        IRepositoryRegistry registry,
        [Description("Name of the indexed collection (repository) to remove.")]
        string collection,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(collection))
            throw new ArgumentException("'collection' is required.");

        var deleted = await RepositoryEndpoints.DeleteCollectionAsync(
            collection, chunkStore, graphStore, registry, cancellationToken);

        return new DeleteCollectionResult(
            deleted,
            collection,
            deleted ? $"Collection '{collection}' was deleted." : $"Collection '{collection}' was not found.");
    }
}

/// <summary>
/// Result of a <c>delete_collection</c> call: whether an entry actually existed and was removed
/// (mirroring the REST endpoint's 204 vs 404), the collection name, and a human-readable message.
/// </summary>
public sealed record DeleteCollectionResult(bool Deleted, string Collection, string Message);
