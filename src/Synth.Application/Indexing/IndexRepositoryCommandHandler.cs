using Microsoft.Extensions.Logging;
using Synth.Application.Cqrs;
using Synth.Application.Vcs;
using Synth.Domain;
using Synth.Domain.Vcs;

namespace Synth.Application.Indexing;

/// <summary>
/// Handles <see cref="IndexRepositoryCommand"/>: the shared "validate the request, reserve the single
/// job slot, and dispatch the detached clone+index+registry-upsert work" flow behind both
/// <c>POST /index</c> and the <c>index_code</c> MCP tool (SYNTH-36). Validation (exactly one of
/// path/repoUrl, directory-exists, repo-URL parse) and the <see cref="IIndexJobTracker.TryStart"/>
/// reservation run synchronously; the actual work is dispatched fire-and-forget on a detached task so
/// this returns immediately. Returns an <see cref="IndexStartOutcome"/> the caller maps to its own
/// response shape. SYNTH-61 lifted this out of <c>IndexingEndpoints.StartIndexing</c> unchanged so it
/// lives behind the CQRS seam (issue #82) — the dependencies it used to take as method parameters are
/// now constructor-injected, but the flow is identical.
/// </summary>
public sealed class IndexRepositoryCommandHandler
    : ICommandHandler<IndexRepositoryCommand, IndexStartOutcome>
{
    private readonly IndexingPipeline _pipeline;
    private readonly IGitRepoService _gitRepoService;
    private readonly IRepositoryRegistry _registry;
    private readonly ICodeChunkStore _store;
    private readonly IIndexJobTracker _tracker;
    private readonly ILogger _logger;

    public IndexRepositoryCommandHandler(
        IndexingPipeline pipeline,
        IGitRepoService gitRepoService,
        IRepositoryRegistry registry,
        ICodeChunkStore store,
        IIndexJobTracker tracker,
        ILoggerFactory loggerFactory)
    {
        _pipeline = pipeline;
        _gitRepoService = gitRepoService;
        _registry = registry;
        _store = store;
        _tracker = tracker;
        _logger = loggerFactory.CreateLogger(typeof(IndexRepositoryCommandHandler).FullName!);
    }

    public Task<IndexStartOutcome> HandleAsync(
        IndexRepositoryCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Start(command));

    private IndexStartOutcome Start(IndexRepositoryCommand command)
    {
        var hasPath = !string.IsNullOrWhiteSpace(command.Path);
        var hasRepoUrl = !string.IsNullOrWhiteSpace(command.RepoUrl);
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

        // Parsed remote-repo URL, carried into the background run so it can build per-chunk blob URLs
        // (SYNTH-40). Stays null for the local-path case (no remote URL, so no source links).
        RepoUrlInfo? repoInfo = null;

        if (hasPath)
        {
            if (!Directory.Exists(command.Path))
                return IndexStartOutcome.ValidationError($"Directory not found: {command.Path}");

            // Local-path indexing lands in the default collection, unchanged from before SYNTH-19.
            collection = CollectionNames.Default;
            source = command.Path!;
            localRoot = command.Path!;
            entry = new RepositoryEntry
            {
                Collection = collection,
                SourceType = "local",
                Source = command.Path!,
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
                info = RepoUrlInfo.Parse(command.RepoUrl!);
            }
            catch (FormatException ex)
            {
                return IndexStartOutcome.ValidationError(ex.Message);
            }

            repoInfo = info;

            // Per-repo collection derived from the URL (SYNTH-18); the same URL always maps here.
            collection = info.Slug;
            source = command.RepoUrl!;
            localRoot = null;
            repoUrl = command.RepoUrl!;
            branch = string.IsNullOrWhiteSpace(command.Branch) ? null : command.Branch;
            entry = new RepositoryEntry
            {
                Collection = collection,
                SourceType = SourceTypeFor(info.Provider),
                Source = command.RepoUrl!,
                Branch = branch,
                LastIndexedAt = DateTime.UtcNow,
                ChunkCount = 0,
            };
        }

        // Reserve the single job slot. A job already in progress is rejected without dispatching
        // anything (SYNTH-30's tracker is the source of truth for "one job at a time").
        if (!_tracker.TryStart(collection, source))
            return IndexStartOutcome.AlreadyRunning();

        // Detached background run: the caller no longer awaits the clone/indexing work. The request's
        // CancellationToken is deliberately NOT used — it is cancelled when the (now near-instant)
        // response completes, which would kill the background job. Use CancellationToken.None so the
        // run lives for as long as it needs.
        _ = Task.Run(async () =>
        {
            try
            {
                var indexRoot = localRoot
                    ?? await _gitRepoService.EnsureRepoAsync(repoUrl!, branch, CancellationToken.None);

                var progress = new Progress<IndexingProgress>(p =>
                    _tracker.ReportProgress(p.FilesIndexed, p.FilesSkipped, p.TotalFiles));

                // repoInfo/branch are non-null only for the repoUrl branch; the local-path case leaves
                // them null so SourceUrl stays null on every chunk (SYNTH-40).
                var summary = await _pipeline.IndexDirectoryAsync(
                    collection, indexRoot, CancellationToken.None, progress, repoInfo, branch);

                // Registry upsert moved here from the endpoint body: it can no longer happen before
                // the response is sent, so it runs once the work actually finished. ChunkCount reads
                // the store's true total rather than summary.ChunksIndexed: the latter is only this
                // run's newly-(re-)embedded delta, which is legitimately 0 on an incremental reindex
                // where every file was unchanged — using it here would clobber the displayed count to
                // zero even though the collection still holds every chunk from the prior run.
                var chunkCount = await _store.CountAsync(collection, CancellationToken.None);
                await _registry.UpsertAsync(
                    entry with { ChunkCount = chunkCount, LastIndexedAt = DateTime.UtcNow },
                    CancellationToken.None);

                _tracker.Complete(summary.FilesIndexed, summary.FilesSkipped, summary.ChunksIndexed);
            }
            catch (Exception ex)
            {
                // Record the failure on the tracker and log it; never let an unobserved task
                // exception escape and tear down the process.
                _logger.LogError(ex, "Background indexing of collection {Collection} failed.", collection);
                _tracker.Fail(ex.Message);
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
