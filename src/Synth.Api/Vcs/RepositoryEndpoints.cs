using Synth.Core;
using Synth.Core.Graph;

namespace Synth.Api.Vcs;

/// <summary>
/// Maps <c>GET /repositories</c>, exposing the <see cref="IRepositoryRegistry"/> so clients can
/// discover the known collections and their metadata (the Vue collection picker in SYNTH-20, and
/// callers that need a valid collection name to pass to search), plus
/// <c>DELETE /repositories/{collection}</c> to remove an indexed collection entirely.
/// </summary>
public static class RepositoryEndpoints
{
    public static IEndpointRouteBuilder MapRepositoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/repositories", async (IRepositoryRegistry registry, CancellationToken cancellationToken) =>
            Results.Ok(await registry.ListAsync(cancellationToken)));

        // Removes an indexed collection completely: its vector-store collection, its call-graph edges
        // (ReplaceEdgesAsync with an empty list is a full clear), and its registry entry. The store and
        // graph cleanup run even when the registry has no entry (the two could drift), but the response
        // is 404 when the registry had nothing to delete, so "delete something that doesn't exist" reads
        // correctly to the caller. Bare route (no /api prefix) — the client's Vite proxy strips /api.
        endpoints.MapDelete("/repositories/{collection}", async (
            string collection,
            ICodeChunkStore chunkStore,
            ICodeGraphStore graphStore,
            IRepositoryRegistry registry,
            CancellationToken cancellationToken) =>
        {
            await chunkStore.DeleteCollectionAsync(collection, cancellationToken);
            await graphStore.ReplaceEdgesAsync(collection, [], cancellationToken);
            var removed = await registry.DeleteAsync(collection, cancellationToken);

            return removed ? Results.NoContent() : Results.NotFound();
        });

        return endpoints;
    }
}
