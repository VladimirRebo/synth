using Synth.Core;
using Synth.Core.Graph;
using Synth.Core.Vcs;

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
        // GET /repositories?limit=&offset= — both optional. Omitting both returns everything (the
        // client's picker relies on this backward-compatible default); when provided they slice the
        // list. ListAsync's order is not stable (ConcurrentDictionary enumeration in-memory, an
        // unsorted Mongo Find otherwise), so we impose a deterministic most-recently-indexed-first
        // order before slicing, so limit/offset page through a repeatable sequence.
        endpoints.MapGet("/repositories", async (
            IRepositoryRegistry registry,
            int? limit,
            int? offset,
            CancellationToken cancellationToken) =>
        {
            if (offset is < 0)
                return Results.BadRequest(new { error = $"'offset' must be non-negative: {offset}" });
            if (limit is < 0)
                return Results.BadRequest(new { error = $"'limit' must be non-negative: {limit}" });

            var repositories = await registry.ListAsync(cancellationToken);
            IEnumerable<RepositoryEntry> ordered = repositories.OrderByDescending(r => r.LastIndexedAt);

            if (offset is { } skip)
                ordered = ordered.Skip(skip);
            if (limit is { } take)
                ordered = ordered.Take(take);

            return Results.Ok(ordered.ToArray());
        });

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
            GitRepoService gitRepoService,
            CancellationToken cancellationToken) =>
        {
            // Read the entry before the delete sequence removes it, so we know its SourceType: only a
            // cloned remote (github/gitlab) has an on-disk checkout under the workspace root to remove;
            // a local source was indexed in place and never cloned.
            var entries = await registry.ListAsync(cancellationToken);
            var entry = entries.FirstOrDefault(e =>
                string.Equals(e.Collection, collection, StringComparison.Ordinal));

            var removed = await DeleteCollectionAsync(
                collection, chunkStore, graphStore, registry, cancellationToken);

            if (removed && entry is not null && IsClonedRemote(entry.SourceType))
                DeleteCheckout(gitRepoService.ResolveCheckoutPath(collection));

            return removed ? Results.NoContent() : Results.NotFound();
        });

        return endpoints;
    }

    /// <summary>
    /// Removes an indexed collection completely — its vector-store collection, its call-graph edges
    /// (a full clear via <see cref="ICodeGraphStore.ReplaceEdgesAsync"/> with an empty list), and its
    /// registry entry — and reports whether the registry actually held an entry to remove. Factored
    /// out of the <c>DELETE /repositories/{collection}</c> handler so the <c>delete_collection</c> MCP
    /// tool (SYNTH-43) drives the exact same three-step sequence rather than duplicating it. The store
    /// and graph cleanup run even when the registry has no entry (the two could drift); a
    /// <c>false</c> return maps to the REST endpoint's 404 for "delete something that doesn't exist".
    /// </summary>
    public static async Task<bool> DeleteCollectionAsync(
        string collection,
        ICodeChunkStore chunkStore,
        ICodeGraphStore graphStore,
        IRepositoryRegistry registry,
        CancellationToken cancellationToken = default)
    {
        await chunkStore.DeleteCollectionAsync(collection, cancellationToken);
        await graphStore.ReplaceEdgesAsync(collection, [], cancellationToken);
        return await registry.DeleteAsync(collection, cancellationToken);
    }

    /// <summary>
    /// True for source types that <see cref="GitRepoService"/> clones into the workspace root
    /// (<c>github</c>/<c>gitlab</c>), i.e. those with an on-disk checkout to clean up. A <c>local</c>
    /// source is indexed in place and has none.
    /// </summary>
    internal static bool IsClonedRemote(string sourceType) =>
        string.Equals(sourceType, "github", StringComparison.Ordinal) ||
        string.Equals(sourceType, "gitlab", StringComparison.Ordinal);

    /// <summary>
    /// Removes an on-disk checkout directory (recursively), tolerating an already-gone directory — the
    /// checkout may have been deleted out-of-band or never fully cloned. Shared by the DELETE handler
    /// and the startup orphan sweep (<see cref="OrphanCheckoutSweeper"/>).
    /// </summary>
    internal static void DeleteCheckout(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Raced with another removal between the Exists check and the delete — already gone.
        }
    }
}
