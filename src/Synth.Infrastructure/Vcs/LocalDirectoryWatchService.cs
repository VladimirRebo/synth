using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Synth.Application.Cqrs;
using Synth.Application.Indexing;
using Synth.Domain.Vcs;

namespace Synth.Infrastructure.Vcs;

/// <summary>
/// Watches every locally-indexed collection's on-disk directory and reindexes it a few seconds after
/// the last detected change — <see cref="RepositoryPollingService"/>'s counterpart for local paths,
/// which have no remote to poll (<c>git ls-remote</c> needs a remote; a local directory only has a
/// filesystem to watch). Without this, editing a locally-indexed project never refreshes search
/// results until the collection is manually reindexed again.
/// </summary>
/// <remarks>
/// One <see cref="FileSystemWatcher"/> per locally-indexed collection, kept in sync with the registry
/// on a fixed interval so a newly-indexed local directory starts
/// being watched, and a deleted/no-longer-local one stops, without a restart. A change anywhere under
/// the directory (skipping <c>bin/</c>, <c>obj/</c>, <c>.git/</c>, <c>node_modules/</c> — the same
/// directories <see cref="Synth.Application.IndexingPipeline"/> itself never descends into) (re)starts
/// a per-collection debounce timer; only the last change in a burst (an editor's autosave, a build,
/// a branch switch touching many files) actually dispatches a reindex, via the same
/// <see cref="IndexRepositoryCommand"/> <c>POST /index</c> and the poller use — so it shares that
/// command's validation and <see cref="Application.Indexing.IIndexJobTracker"/> single-job-slot guard.
/// </remarks>
public sealed class LocalDirectoryWatchService : BackgroundService
{
    private static readonly TimeSpan DefaultRegistrySyncInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultDebounceDelay = TimeSpan.FromSeconds(3);
    private static readonly string[] IgnoredSegments = ["bin", "obj", ".git", "node_modules"];

    private readonly IRepositoryRegistry _registry;
    private readonly ICommandHandler<IndexRepositoryCommand, IndexStartOutcome> _indexHandler;
    private readonly ILogger<LocalDirectoryWatchService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _registrySyncInterval;
    private readonly TimeSpan _debounceDelay;

    // Collection -> its watcher. Touched only from ExecuteAsync's single loop thread, so no locking.
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();

    // Collection -> its pending debounce timer. Touched from watcher event callbacks, which can run
    // concurrently on arbitrary thread-pool threads, so this one does need to be concurrency-safe.
    private readonly ConcurrentDictionary<string, Timer> _pendingReindexes = new();

    public LocalDirectoryWatchService(
        IRepositoryRegistry registry,
        ICommandHandler<IndexRepositoryCommand, IndexStartOutcome> indexHandler,
        ILogger<LocalDirectoryWatchService> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? registrySyncInterval = null,
        TimeSpan? debounceDelay = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _indexHandler = indexHandler ?? throw new ArgumentNullException(nameof(indexHandler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _registrySyncInterval = registrySyncInterval ?? DefaultRegistrySyncInterval;
        // The debounce timer runs on real wall-clock time regardless of _timeProvider (a plain
        // System.Threading.Timer, not driven by TimeProvider.CreateTimer) — tests that need to
        // observe a debounced reindex pass a short real delay here instead of faking time.
        _debounceDelay = debounceDelay ?? DefaultDebounceDelay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncWatchersAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // A failed sync must never take the host down — the next tick retries from scratch.
                _logger.LogWarning(ex, "Failed to sync local-directory watchers.");
            }

            try
            {
                await Task.Delay(_registrySyncInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        foreach (var collection in _watchers.Keys.ToList())
            RemoveWatcher(collection);
    }

    // Adds a watcher for every newly-registered local collection and removes one for every collection
    // that's no longer registered (deleted) or no longer local (source type changed, which today
    // never happens post-creation, but costs nothing to handle correctly).
    private async Task SyncWatchersAsync(CancellationToken cancellationToken)
    {
        var entries = await _registry.ListAsync(cancellationToken);
        var localPathByCollection = entries
            .Where(e => string.Equals(e.SourceType, "local", StringComparison.Ordinal))
            .ToDictionary(e => e.Collection, e => e.Source, StringComparer.Ordinal);

        foreach (var collection in _watchers.Keys.Except(localPathByCollection.Keys).ToList())
            RemoveWatcher(collection);

        foreach (var (collection, path) in localPathByCollection)
        {
            if (_watchers.ContainsKey(collection) || !Directory.Exists(path))
                continue;

            _watchers[collection] = CreateWatcher(collection, path);
        }
    }

    private FileSystemWatcher CreateWatcher(string collection, string path)
    {
        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
        };

        watcher.Changed += (_, e) => OnFileEvent(collection, path, e.FullPath);
        watcher.Created += (_, e) => OnFileEvent(collection, path, e.FullPath);
        watcher.Deleted += (_, e) => OnFileEvent(collection, path, e.FullPath);
        watcher.Renamed += (_, e) => OnFileEvent(collection, path, e.FullPath);

        // A watcher can fail (e.g. the OS's internal notification buffer overflowed under a very
        // large burst of changes) without throwing anywhere catchable — log it and keep going; the
        // directory just misses events until the next registry sync recreates the watcher.
        watcher.Error += (_, e) => _logger.LogWarning(
            e.GetException(), "File watcher for '{Collection}' failed; local changes may be missed until it is recreated.", collection);

        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void RemoveWatcher(string collection)
    {
        if (_watchers.Remove(collection, out var watcher))
            watcher.Dispose();

        if (_pendingReindexes.TryRemove(collection, out var timer))
            timer.Dispose();
    }

    private void OnFileEvent(string collection, string root, string changedPath)
    {
        if (IsIgnored(root, changedPath))
            return;

        ScheduleReindex(collection, root);
    }

    // (Re)starts the debounce timer so a burst of changes (an editor autosave writing several files,
    // a build, a branch switch) collapses into exactly one reindex, fired once things go quiet for
    // _debounceDelay rather than once per individual file event. Also used by TriggerReindex to retry
    // after losing the single-job-slot race (see there) — same debounce delay doubles as a backoff.
    private void ScheduleReindex(string collection, string path)
    {
        _pendingReindexes.AddOrUpdate(
            collection,
            _ => new Timer(_ => TriggerReindex(collection, path), null, _debounceDelay, Timeout.InfiniteTimeSpan),
            (_, existing) =>
            {
                existing.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
                return existing;
            });
    }

    // Mirrors the directories IndexingPipeline's own walk never descends into — no point reindexing
    // over build output or VCS metadata churn, which would otherwise retrigger on nearly every build.
    private static bool IsIgnored(string root, string changedPath)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(root, changedPath);
        }
        catch (ArgumentException)
        {
            return false; // Paths on different roots (shouldn't happen) — don't ignore, be safe.
        }

        return relative
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
            .Any(segment => IgnoredSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private async void TriggerReindex(string collection, string path)
    {
        // The timer already fired (that's why we're here); drop our own reference to it so a
        // concurrent RemoveWatcher (registry sync noticing the collection is gone) doesn't dispose a
        // timer that's mid-callback, and so the next file event starts a fresh one.
        if (_pendingReindexes.TryRemove(collection, out var timer))
            timer.Dispose();

        try
        {
            var outcome = await _indexHandler.HandleAsync(new IndexRepositoryCommand(Path: path));
            if (outcome.Status == IndexStartOutcome.Kind.AlreadyRunning)
            {
                // Losing the single-job-slot race (another collection's poll/watch/manual reindex is
                // running) must not drop this change on the floor — the debounce timer that would have
                // retried it already fired and removed itself, so nothing else will ever pick it back
                // up. Re-arm one more debounce cycle instead; this repeats until a run finds the slot
                // free, which is fine since IIndexJobTracker jobs are typically sub-second for a single
                // local directory.
                _logger.LogInformation(
                    "Deferred auto-reindexing '{Collection}': another job is already running; retrying shortly.", collection);
                ScheduleReindex(collection, path);
            }
            else
            {
                _logger.LogInformation("Detected local changes in '{Collection}'; reindexing.", collection);
            }
        }
        catch (Exception ex)
        {
            // A failed auto-reindex must never crash the timer thread — the next file change retries.
            _logger.LogWarning(ex, "Auto-reindex of '{Collection}' failed.", collection);
        }
    }
}
