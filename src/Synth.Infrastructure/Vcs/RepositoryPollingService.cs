using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synth.Application.Cqrs;
using Synth.Application.Indexing;
using Synth.Application.Vcs;
using Synth.Domain.Vcs;

namespace Synth.Infrastructure.Vcs;

/// <summary>
/// Periodically checks every repoUrl-indexed collection's remote for a new commit (<c>git ls-remote</c>
/// via <see cref="IGitRepoService.GetRemoteHeadShaAsync"/> — no clone/fetch) and, on a genuine change,
/// dispatches the same <see cref="IndexRepositoryCommand"/> <c>POST /index</c> and <c>index_code</c>
/// already use. Works for any indexed repository regardless of who owns it — unlike a webhook, polling
/// needs no cooperation from (or admin access to) the remote, and needs no publicly reachable endpoint
/// on this side either, which matters for a personal instance that normally only listens on localhost.
/// </summary>
/// <remarks>
/// The poll interval (<see cref="VcsOptions.PollIntervalMinutes"/>) is re-read from
/// <see cref="IOptionsMonitor{TOptions}"/> on every tick, so a live settings change takes effect
/// without a restart. <c>0</c> (or negative) disables polling — the loop keeps running but just
/// re-checks the setting once a minute instead of ever touching a repository, so re-enabling it later
/// still needs no restart. No queue and no per-repo concurrency: entries are checked one at a time:
/// a slow/hung remote for one repository delays the others but can never corrupt their state, and if a
/// reindex is already running (<see cref="IIndexJobTracker"/>'s single job slot) this tick's dispatch
/// is simply reported <see cref="IndexStartOutcome.Kind.AlreadyRunning"/> and dropped — the next tick
/// (or a manual reindex) catches up, mirroring the same tradeoff the earlier webhook design made.
/// </remarks>
public sealed class RepositoryPollingService : BackgroundService
{
    private static readonly TimeSpan DisabledRecheckInterval = TimeSpan.FromMinutes(1);

    private readonly IRepositoryRegistry _registry;
    private readonly IGitRepoService _gitRepoService;
    private readonly IRepositoryPollState _pollState;
    private readonly ICommandHandler<IndexRepositoryCommand, IndexStartOutcome> _indexHandler;
    private readonly IOptionsMonitor<VcsOptions> _options;
    private readonly ILogger<RepositoryPollingService> _logger;
    private readonly TimeProvider _timeProvider;

    public RepositoryPollingService(
        IRepositoryRegistry registry,
        IGitRepoService gitRepoService,
        IRepositoryPollState pollState,
        ICommandHandler<IndexRepositoryCommand, IndexStartOutcome> indexHandler,
        IOptionsMonitor<VcsOptions> options,
        ILogger<RepositoryPollingService> logger,
        TimeProvider? timeProvider = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _gitRepoService = gitRepoService ?? throw new ArgumentNullException(nameof(gitRepoService));
        _pollState = pollState ?? throw new ArgumentNullException(nameof(pollState));
        _indexHandler = indexHandler ?? throw new ArgumentNullException(nameof(indexHandler));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalMinutes = _options.CurrentValue.PollIntervalMinutes;

            // Delay-first, not poll-first: a freshly started process (or a short-lived
            // WebApplicationFactory test host, which never outlives one interval) never fires a real
            // git ls-remote at t=0 — the first tick only happens after a full interval has elapsed.
            await DelayAsync(intervalMinutes <= 0 ? DisabledRecheckInterval : TimeSpan.FromMinutes(intervalMinutes), stoppingToken);

            if (stoppingToken.IsCancellationRequested || intervalMinutes <= 0)
                continue;

            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed tick must never take the host down — the next tick retries from scratch.
                _logger.LogWarning(ex, "Repository poll tick failed.");
            }
        }
    }

    /// <summary>
    /// Runs one poll tick over every currently-registered collection. Public so it can be driven
    /// synchronously in tests without hosting the background loop; <see cref="ExecuteAsync"/> just
    /// calls it on a timer.
    /// </summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var entries = await _registry.ListAsync(cancellationToken);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A local-path source has no remote to poll.
            if (string.Equals(entry.SourceType, "local", StringComparison.Ordinal))
                continue;

            await PollEntryAsync(entry, cancellationToken);
        }
    }

    private async Task PollEntryAsync(RepositoryEntry entry, CancellationToken cancellationToken)
    {
        string? remoteSha;
        try
        {
            remoteSha = await _gitRepoService.GetRemoteHeadShaAsync(entry.Source, entry.Branch, cancellationToken);
        }
        catch (Exception ex)
        {
            // One unreachable/renamed repo must not stop the rest of this tick from running.
            _logger.LogWarning(ex, "Could not check '{Collection}' for updates.", entry.Collection);
            return;
        }

        if (remoteSha is null)
            return; // branch/ref not found upstream — nothing sensible to compare against

        var lastKnown = await _pollState.GetLastKnownShaAsync(entry.Collection, cancellationToken);
        if (string.Equals(lastKnown, remoteSha, StringComparison.Ordinal))
            return; // unchanged since the last tick

        await _pollState.SetLastKnownShaAsync(entry.Collection, remoteSha, cancellationToken);

        // First observation for this collection (fresh install, or a poll-state table just created
        // for a collection indexed before this feature existed): the content indexed at index-time
        // already matches this SHA, so record the baseline without reindexing — only a *change* from
        // a previously observed SHA is a real reason to reindex.
        if (lastKnown is null)
            return;

        _logger.LogInformation(
            "Detected a new commit on '{Collection}' ({Old} -> {New}); reindexing.",
            entry.Collection, lastKnown, remoteSha);

        var outcome = await _indexHandler.HandleAsync(
            new IndexRepositoryCommand(RepoUrl: entry.Source, Branch: entry.Branch), cancellationToken);

        if (outcome.Status == IndexStartOutcome.Kind.AlreadyRunning)
        {
            _logger.LogInformation(
                "Skipped reindexing '{Collection}': another job is already running.", entry.Collection);
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown while waiting — the ExecuteAsync loop condition ends it on the next check.
        }
    }
}
