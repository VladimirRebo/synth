using Synth.Application.Cqrs;
using Synth.Application.Indexing;

namespace Synth.Api.Indexing;

/// <summary>
/// Maps the manual indexing trigger and its progress endpoint. SYNTH-19 added the remote-repo branch
/// and registry bookkeeping. SYNTH-31 turned <c>POST /index</c> fire-and-forget: request validation
/// (exactly one of path/repoUrl, directory-exists, repo-URL parse) still runs synchronously and
/// returns <c>400</c> immediately, but the actual clone + indexing work is dispatched as a detached
/// background task so the HTTP response returns <c>202 Accepted</c> at once. Callers observe progress
/// via <c>GET /index/status</c> (backed by <see cref="IIndexJobTracker"/>) instead of holding the
/// response open. The tracker also enforces one job at a time (a concurrent start gets <c>409</c>).
/// SYNTH-61 moved the shared "try to start" flow into <see cref="IndexRepositoryCommandHandler"/>
/// behind the CQRS seam; this endpoint now binds the request as an <see cref="IndexRepositoryCommand"/>
/// and dispatches it, mapping the <see cref="IndexStartOutcome"/> to a status code. Still Minimal API.
/// </summary>
public static class IndexingEndpoints
{
    public static IEndpointRouteBuilder MapIndexingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/index", async (
            IndexRepositoryCommand command,
            ICommandHandler<IndexRepositoryCommand, IndexStartOutcome> handler) =>
        {
            var outcome = await handler.HandleAsync(command);
            return outcome.Status switch
            {
                IndexStartOutcome.Kind.ValidationError => Results.BadRequest(new { error = outcome.Error }),
                IndexStartOutcome.Kind.AlreadyRunning => Results.Conflict(new { error = outcome.Error }),
                _ => Results.Accepted(value: new { collection = outcome.Collection, status = "started" }),
            };
        });

        // Poll target for the fire-and-forget POST /index above: returns the single current-or-most-recent
        // job snapshot. Bare route (no /api prefix), matching every other endpoint in this app.
        endpoints.MapGet("/index/status", (IIndexJobTracker tracker) => Results.Ok(tracker.Current));

        return endpoints;
    }
}
