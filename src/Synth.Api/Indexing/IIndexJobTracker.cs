using Synth.Core.Indexing;

namespace Synth.Api.Indexing;

/// <summary>
/// Tracks the single current-or-most-recent index job so a client can poll indexing progress instead
/// of holding the <c>POST /index</c> response open (issue #39). In-memory only — this is ephemeral,
/// process-lifetime state, not history, so there is no Mongo backing. Exactly one job is tracked
/// (Synth is a personal, single-user tool); wiring <c>POST /index</c> to use this and exposing a
/// status endpoint is SYNTH-31.
/// </summary>
public interface IIndexJobTracker
{
    /// <summary>The current/most-recent job; <see cref="IndexJobStatus.Idle"/> when nothing has run.</summary>
    IndexJobStatus Current { get; }

    /// <summary>
    /// Atomically transitions to <see cref="IndexJobState.Running"/> if no job is already running,
    /// returning <c>true</c>. If a job is in progress, leaves it untouched and returns <c>false</c>
    /// (SYNTH-31 uses this to reject a concurrent <c>POST /index</c> with 409).
    /// </summary>
    bool TryStart(string collection, string source);

    /// <summary>Updates the in-flight job's file counters. No-op if no job is running.</summary>
    void ReportProgress(int filesIndexed, int filesSkipped, int? totalFiles);

    /// <summary>Transitions the job to <see cref="IndexJobState.Done"/> with final counts.</summary>
    void Complete(int filesIndexed, int filesSkipped, int chunksIndexed);

    /// <summary>Transitions the job to <see cref="IndexJobState.Failed"/> with an error message.</summary>
    void Fail(string error);
}

/// <summary>
/// Process-local <see cref="IIndexJobTracker"/> guarding a single mutable status behind a lock —
/// updates are infrequent relative to embedding calls, so a plain lock is enough (no need to be
/// lock-free). Registered as a DI singleton.
/// </summary>
public sealed class InMemoryIndexJobTracker : IIndexJobTracker
{
    private readonly object _gate = new();
    private IndexJobStatus _current = IndexJobStatus.Idle;

    public IndexJobStatus Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public bool TryStart(string collection, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);

        lock (_gate)
        {
            if (_current.State == IndexJobState.Running)
                return false;

            _current = new IndexJobStatus
            {
                State = IndexJobState.Running,
                Collection = collection,
                Source = source ?? string.Empty,
                StartedAt = DateTime.UtcNow,
            };
            return true;
        }
    }

    public void ReportProgress(int filesIndexed, int filesSkipped, int? totalFiles)
    {
        lock (_gate)
        {
            if (_current.State != IndexJobState.Running)
                return;

            _current = _current with
            {
                FilesIndexed = filesIndexed,
                FilesSkipped = filesSkipped,
                // A null report keeps the previously counted total rather than clearing it.
                TotalFiles = totalFiles ?? _current.TotalFiles,
            };
        }
    }

    public void Complete(int filesIndexed, int filesSkipped, int chunksIndexed)
    {
        lock (_gate)
        {
            _current = _current with
            {
                State = IndexJobState.Done,
                FilesIndexed = filesIndexed,
                FilesSkipped = filesSkipped,
                ChunksIndexed = chunksIndexed,
                FinishedAt = DateTime.UtcNow,
                Error = null,
            };
        }
    }

    public void Fail(string error)
    {
        lock (_gate)
        {
            _current = _current with
            {
                State = IndexJobState.Failed,
                Error = error,
                FinishedAt = DateTime.UtcNow,
            };
        }
    }
}
