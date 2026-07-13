using Microsoft.AspNetCore.Mvc;
using Synth.Application.Cqrs;
using Synth.Application.Vcs;
using Synth.Domain.Vcs;

namespace Synth.Api.Vcs;

/// <summary>
/// The repository-registry endpoints: <c>GET /repositories</c> (list the known collections and their
/// metadata — backing the Vue collection picker from SYNTH-20 and any caller that needs a valid
/// collection name) and <c>DELETE /repositories/{collection}</c> (remove an indexed collection
/// entirely). The read stays a thin action over <see cref="IRepositoryRegistry"/> — no Query wrapper,
/// same judgment call as <see cref="Synth.Api.Search.SearchController"/>'s reads — while the delete's
/// real multi-step logic lives behind the CQRS seam in
/// <see cref="DeleteCollectionCommandHandler"/> (issue #82), shared with the <c>delete_collection</c>
/// MCP tool. SYNTH-67 converted this from the Minimal-API <c>RepositoryEndpoints</c> to a Controller
/// (issue #82, slice 12).
/// </summary>
/// <remarks>
/// Routes stay bare (no <c>/api</c> prefix, no class-level <c>[Route]</c>) — each action carries its
/// own absolute route, and the client's Vite proxy strips <c>/api</c>.
/// </remarks>
[ApiController]
public class RepositoriesController : ControllerBase
{
    private readonly ICommandHandler<DeleteCollectionCommand, bool> _deleteHandler;

    public RepositoriesController(ICommandHandler<DeleteCollectionCommand, bool> deleteHandler)
    {
        _deleteHandler = deleteHandler;
    }

    /// <summary>
    /// Lists the known collections and their metadata. <c>limit</c>/<c>offset</c> are both optional;
    /// omitting both returns everything (the client's picker relies on this backward-compatible
    /// default), and when provided they slice the list. <see cref="IRepositoryRegistry.ListAsync"/>'s
    /// order is not stable (ConcurrentDictionary enumeration in-memory, an unsorted Mongo Find
    /// otherwise), so a deterministic most-recently-indexed-first order is imposed before slicing, so
    /// limit/offset page through a repeatable sequence.
    /// </summary>
    [HttpGet("/repositories")]
    public async Task<IActionResult> List(
        [FromServices] IRepositoryRegistry registry,
        int? limit,
        int? offset,
        CancellationToken cancellationToken)
    {
        if (offset is < 0)
            return BadRequest(new { error = $"'offset' must be non-negative: {offset}" });
        if (limit is < 0)
            return BadRequest(new { error = $"'limit' must be non-negative: {limit}" });

        var repositories = await registry.ListAsync(cancellationToken);
        IEnumerable<RepositoryEntry> ordered = repositories.OrderByDescending(r => r.LastIndexedAt);

        if (offset is { } skip)
            ordered = ordered.Skip(skip);
        if (limit is { } take)
            ordered = ordered.Take(take);

        return Ok(ordered.ToArray());
    }

    /// <summary>
    /// Removes an indexed collection completely by dispatching a <see cref="DeleteCollectionCommand"/>:
    /// its vector-store collection, its call-graph edges and its registry entry (plus the on-disk
    /// checkout for a cloned remote). Maps the handler's result to <c>204 No Content</c> when the
    /// registry held an entry to remove, or <c>404 Not Found</c> when it did not — so "delete something
    /// that doesn't exist" reads correctly to the caller.
    /// </summary>
    [HttpDelete("/repositories/{collection}")]
    public async Task<IActionResult> Delete(string collection, CancellationToken cancellationToken)
    {
        var removed = await _deleteHandler.HandleAsync(
            new DeleteCollectionCommand(collection), cancellationToken);

        return removed ? NoContent() : NotFound();
    }
}
