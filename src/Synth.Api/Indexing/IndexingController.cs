using Microsoft.AspNetCore.Mvc;
using Synth.Application.Cqrs;
using Synth.Application.Indexing;

namespace Synth.Api.Indexing;

/// <summary>
/// The manual indexing trigger and its progress endpoint. SYNTH-19 added the remote-repo branch
/// and registry bookkeeping. SYNTH-31 turned <c>POST /index</c> fire-and-forget: request validation
/// (exactly one of path/repoUrl, directory-exists, repo-URL parse) still runs synchronously and
/// returns <c>400</c> immediately, but the actual clone + indexing work is dispatched as a detached
/// background task so the HTTP response returns <c>202 Accepted</c> at once. Callers observe progress
/// via <c>GET /index/status</c> (backed by <see cref="IIndexJobTracker"/>) instead of holding the
/// response open. The tracker also enforces one job at a time (a concurrent start gets <c>409</c>).
/// SYNTH-61 moved the shared "try to start" flow into <see cref="IndexRepositoryCommandHandler"/>
/// behind the CQRS seam; this action binds the request as an <see cref="IndexRepositoryCommand"/>
/// and dispatches it, mapping the <see cref="IndexStartOutcome"/> to a status code. SYNTH-66
/// converted this from a Minimal API to a Controller (issue #82, slice 11).
/// </summary>
/// <remarks>
/// Routes stay bare (no <c>/api</c> prefix, no class-level <c>[Route]</c>) — each action carries its
/// own absolute route, and the client's Vite proxy strips <c>/api</c>.
/// </remarks>
[ApiController]
public class IndexingController : ControllerBase
{
    private readonly ICommandHandler<IndexRepositoryCommand, IndexStartOutcome> _handler;
    private readonly IIndexJobTracker _tracker;

    public IndexingController(
        ICommandHandler<IndexRepositoryCommand, IndexStartOutcome> handler,
        IIndexJobTracker tracker)
    {
        _handler = handler;
        _tracker = tracker;
    }

    /// <summary>
    /// Fire-and-forget indexing trigger. Dispatches the request as an
    /// <see cref="IndexRepositoryCommand"/>; synchronous validation failures map to <c>400</c>, a
    /// concurrent job to <c>409</c>, and a successful start to <c>202 Accepted</c> carrying only
    /// <c>{collection, status}</c> — progress is polled via <see cref="Status"/>.
    /// <c>[FromBody]</c> is explicit: under <c>[ApiController]</c> complex types are inferred from the
    /// body, but stating it keeps the binding source unambiguous after the Minimal API move.
    /// </summary>
    [HttpPost("/index")]
    public async Task<IActionResult> Index([FromBody] IndexRepositoryCommand command)
    {
        var outcome = await _handler.HandleAsync(command);
        return outcome.Status switch
        {
            IndexStartOutcome.Kind.ValidationError => BadRequest(new { error = outcome.Error }),
            IndexStartOutcome.Kind.AlreadyRunning => Conflict(new { error = outcome.Error }),
            _ => Accepted(value: new { collection = outcome.Collection, status = "started" }),
        };
    }

    /// <summary>
    /// Poll target for the fire-and-forget <see cref="Index"/> above: returns the single
    /// current-or-most-recent job snapshot. Bare route (no /api prefix), matching every other
    /// endpoint in this app.
    /// </summary>
    [HttpGet("/index/status")]
    public IActionResult Status() => Ok(_tracker.Current);
}
