using System.ComponentModel;
using ModelContextProtocol.Server;
using Synth.Application.Cqrs;
using Synth.Application.Vcs;

namespace Synth.Api.Mcp;

/// <summary>
/// Transport-agnostic MCP tool that removes an indexed collection completely — the MCP equivalent of
/// <c>DELETE /repositories/{collection}</c> (SYNTH-34, part of issue #44). A thin wrapper: the wrapping
/// lives here while the actual multi-step removal (vector-store collection, call-graph edges, registry
/// entry, and a cloned remote's checkout) stays in <see cref="DeleteCollectionCommandHandler"/>, shared
/// with the REST endpoint, so the two transports can never drift (SYNTH-67 moved that sequence behind
/// the CQRS seam, mirroring how <c>IndexCodeTool</c> dispatches <c>IndexRepositoryCommand</c>).
/// Registered via <c>AddMcpServer().WithTools&lt;DeleteCollectionTool&gt;()</c> over both the HTTP and
/// stdio transports.
/// </summary>
[McpServerToolType]
public sealed class DeleteCollectionTool
{
    /// <summary>
    /// Deletes <paramref name="collection"/>'s chunks, call-graph edges and registry entry. The command
    /// handler is resolved from DI per invocation; the result mirrors the REST endpoint's 204/404 split
    /// via <see cref="DeleteCollectionResult.Deleted"/>.
    /// </summary>
    [McpServerTool(Name = "delete_collection")]
    [Description(
        "DESTRUCTIVE: permanently remove an indexed collection (repository) from Synth — deletes its " +
        "vector-store collection, its call-graph edges and its registry entry. This cannot be undone; " +
        "the collection must be re-indexed to search it again. Reports deleted=false when no such " +
        "collection existed.")]
    public static async Task<DeleteCollectionResult> DeleteCollectionAsync(
        ICommandHandler<DeleteCollectionCommand, bool> handler,
        [Description("Name of the indexed collection (repository) to remove.")]
        string collection,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(collection))
            throw new ArgumentException("'collection' is required.");

        var deleted = await handler.HandleAsync(
            new DeleteCollectionCommand(collection), cancellationToken);

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
