using Synth.Api.Vcs;
using Synth.Core;
using Synth.Core.Vcs;

namespace Synth.Api.Indexing;

/// <summary>
/// Request body for <c>POST /index</c>. Exactly one of <see cref="Path"/> (a local directory,
/// indexed into <see cref="CollectionNames.Default"/>) or <see cref="RepoUrl"/> (a remote git
/// URL, cloned/fetched and indexed into its own per-repo collection) must be supplied.
/// <see cref="Branch"/> only applies to the <see cref="RepoUrl"/> case.
/// </summary>
public sealed record IndexRequest(string? Path = null, string? RepoUrl = null, string? Branch = null);

/// <summary>
/// Maps the manual indexing trigger and its progress endpoint. SYNTH-19 added the remote-repo branch
/// and registry bookkeeping. SYNTH-31 turned <c>POST /index</c> fire-and-forget: request validation
/// (exactly one of path/repoUrl, directory-exists, repo-URL parse) still runs synchronously and
/// returns <c>400</c> immediately, but the actual clone + indexing work is dispatched as a detached
/// background task so the HTTP response returns <c>202 Accepted</c> at once. Callers observe progress
/// via <c>GET /index/status</c> (backed by <see cref="IIndexJobTracker"/>) instead of holding the
/// response open. The tracker also enforces one job at a time (a concurrent start gets <c>409</c>).
/// </summary>
public static class IndexingEndpoints
{
    public static IEndpointRouteBuilder MapIndexingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/index", (
            IndexRequest request,
            IndexingPipeline pipeline,
            GitRepoService gitRepoService,
            IRepositoryRegistry registry,
            IIndexJobTracker tracker,
            ILoggerFactory loggerFactory) =>
        {
            var hasPath = !string.IsNullOrWhiteSpace(request.Path);
            var hasRepoUrl = !string.IsNullOrWhiteSpace(request.RepoUrl);
            if (hasPath == hasRepoUrl)
                return Results.BadRequest(new { error = "Provide exactly one of 'path' or 'repoUrl'." });

            string collection;
            string source;
            RepositoryEntry entry;

            // The clone root is only known upfront for the local-path case. For the repo-URL case the
            // clone/fetch is part of the background work, so localRoot stays null and the branch/URL are
            // carried into the continuation.
            string? localRoot;
            string? repoUrl = null;
            string? branch = null;

            if (hasPath)
            {
                if (!Directory.Exists(request.Path))
                    return Results.BadRequest(new { error = $"Directory not found: {request.Path}" });

                // Local-path indexing lands in the default collection, unchanged from before SYNTH-19.
                collection = CollectionNames.Default;
                source = request.Path!;
                localRoot = request.Path!;
                entry = new RepositoryEntry
                {
                    Collection = collection,
                    SourceType = "local",
                    Source = request.Path!,
                    Branch = null,
                    LastIndexedAt = DateTime.UtcNow,
                    ChunkCount = 0,
                };
            }
            else
            {
                RepoUrlInfo info;
                try
                {
                    info = RepoUrlInfo.Parse(request.RepoUrl!);
                }
                catch (FormatException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }

                // Per-repo collection derived from the URL (SYNTH-18); the same URL always maps here.
                collection = info.Slug;
                source = request.RepoUrl!;
                localRoot = null;
                repoUrl = request.RepoUrl!;
                branch = string.IsNullOrWhiteSpace(request.Branch) ? null : request.Branch;
                entry = new RepositoryEntry
                {
                    Collection = collection,
                    SourceType = SourceTypeFor(info.Provider),
                    Source = request.RepoUrl!,
                    Branch = branch,
                    LastIndexedAt = DateTime.UtcNow,
                    ChunkCount = 0,
                };
            }

            // Reserve the single job slot. A job already in progress is rejected with 409 without
            // dispatching anything (SYNTH-30's tracker is the source of truth for "one job at a time").
            if (!tracker.TryStart(collection, source))
                return Results.Conflict(new { error = "An indexing job is already running." });

            // Detached background run: the endpoint no longer awaits the clone/indexing work. The
            // request's CancellationToken is deliberately NOT used — it is cancelled when the (now
            // near-instant) response completes, which would kill the background job. Use
            // CancellationToken.None so the run lives for as long as it needs.
            var logger = loggerFactory.CreateLogger(typeof(IndexingEndpoints).FullName!);
            _ = Task.Run(async () =>
            {
                try
                {
                    var indexRoot = localRoot
                        ?? await gitRepoService.EnsureRepoAsync(repoUrl!, branch, CancellationToken.None);

                    var progress = new Progress<IndexingProgress>(p =>
                        tracker.ReportProgress(p.FilesIndexed, p.FilesSkipped, p.TotalFiles));

                    var summary = await pipeline.IndexDirectoryAsync(
                        collection, indexRoot, CancellationToken.None, progress);

                    // Registry upsert moved here from the endpoint body: it can no longer happen before
                    // the response is sent, so it runs once the work actually finished.
                    await registry.UpsertAsync(
                        entry with { ChunkCount = summary.ChunksIndexed, LastIndexedAt = DateTime.UtcNow },
                        CancellationToken.None);

                    tracker.Complete(summary.FilesIndexed, summary.FilesSkipped, summary.ChunksIndexed);
                }
                catch (Exception ex)
                {
                    // Record the failure on the tracker and log it; never let an unobserved task
                    // exception escape and tear down the process.
                    logger.LogError(ex, "Background indexing of collection {Collection} failed.", collection);
                    tracker.Fail(ex.Message);
                }
            });

            return Results.Accepted(value: new { collection, status = "started" });
        });

        // Poll target for the fire-and-forget POST /index above: returns the single current-or-most-recent
        // job snapshot. Bare route (no /api prefix), matching every other endpoint in this app.
        endpoints.MapGet("/index/status", (IIndexJobTracker tracker) => Results.Ok(tracker.Current));

        return endpoints;
    }

    private static string SourceTypeFor(GitProvider provider) => provider switch
    {
        GitProvider.GitHub => "github",
        GitProvider.GitLab => "gitlab",
        _ => "other",
    };
}
