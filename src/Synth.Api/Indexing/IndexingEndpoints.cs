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
/// Discriminated result of <see cref="IndexingEndpoints.StartIndexing"/> — the shared "try to start
/// an indexing job" flow used by both <c>POST /index</c> and the <c>index_code</c> MCP tool. Callers
/// map it to their own response type: <see cref="Kind.Started"/> carries the resolved
/// <see cref="Collection"/>, <see cref="Kind.ValidationError"/> and <see cref="Kind.AlreadyRunning"/>
/// carry a human-readable <see cref="Error"/>.
/// </summary>
public sealed record IndexStartOutcome(IndexStartOutcome.Kind Status, string? Collection, string? Error)
{
    public enum Kind { Started, ValidationError, AlreadyRunning }

    public static IndexStartOutcome Started(string collection) => new(Kind.Started, collection, null);

    public static IndexStartOutcome ValidationError(string message) => new(Kind.ValidationError, null, message);

    public static IndexStartOutcome AlreadyRunning() =>
        new(Kind.AlreadyRunning, null, "An indexing job is already running.");
}

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
            var outcome = StartIndexing(request, pipeline, gitRepoService, registry, tracker, loggerFactory);
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

    /// <summary>
    /// Shared "validate the request, reserve the single job slot, and dispatch the detached
    /// clone+index+registry-upsert work" flow behind both <c>POST /index</c> and the
    /// <c>index_code</c> MCP tool (SYNTH-36). Validation (exactly one of path/repoUrl,
    /// directory-exists, repo-URL parse) and the <see cref="IIndexJobTracker.TryStart"/> reservation
    /// run synchronously; the actual work is dispatched fire-and-forget on a detached task so this
    /// returns immediately. Returns an <see cref="IndexStartOutcome"/> the caller maps to its own
    /// response shape.
    /// </summary>
    public static IndexStartOutcome StartIndexing(
        IndexRequest request,
        IndexingPipeline pipeline,
        GitRepoService gitRepoService,
        IRepositoryRegistry registry,
        IIndexJobTracker tracker,
        ILoggerFactory loggerFactory)
    {
        var hasPath = !string.IsNullOrWhiteSpace(request.Path);
        var hasRepoUrl = !string.IsNullOrWhiteSpace(request.RepoUrl);
        if (hasPath == hasRepoUrl)
            return IndexStartOutcome.ValidationError("Provide exactly one of 'path' or 'repoUrl'.");

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
                return IndexStartOutcome.ValidationError($"Directory not found: {request.Path}");

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
                return IndexStartOutcome.ValidationError(ex.Message);
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

        // Reserve the single job slot. A job already in progress is rejected without dispatching
        // anything (SYNTH-30's tracker is the source of truth for "one job at a time").
        if (!tracker.TryStart(collection, source))
            return IndexStartOutcome.AlreadyRunning();

        // Detached background run: the caller no longer awaits the clone/indexing work. The request's
        // CancellationToken is deliberately NOT used — it is cancelled when the (now near-instant)
        // response completes, which would kill the background job. Use CancellationToken.None so the
        // run lives for as long as it needs.
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

        return IndexStartOutcome.Started(collection);
    }

    private static string SourceTypeFor(GitProvider provider) => provider switch
    {
        GitProvider.GitHub => "github",
        GitProvider.GitLab => "gitlab",
        _ => "other",
    };
}
