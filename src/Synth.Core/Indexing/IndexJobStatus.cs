namespace Synth.Core.Indexing;

/// <summary>
/// Lifecycle state of the single tracked index job. There is exactly one "current or most recently
/// finished" job process-wide (Synth is a personal, single-user tool — no per-collection history or
/// job queue, per issue #39).
/// </summary>
public enum IndexJobState
{
    /// <summary>Nothing has ever been indexed in this process.</summary>
    Idle,

    /// <summary>An index run is currently in progress.</summary>
    Running,

    /// <summary>The most recent run completed successfully.</summary>
    Done,

    /// <summary>The most recent run threw and was recorded as failed.</summary>
    Failed,
}

/// <summary>
/// Immutable snapshot of the current/most-recent index job, surfaced so a client can observe indexing
/// progress without holding the <c>POST /index</c> HTTP response open (issue #39). This is ephemeral,
/// process-lifetime state — never persisted — produced by <c>IIndexJobTracker</c>. When nothing has
/// ever run the snapshot is <see cref="Idle"/> with no other fields populated.
/// </summary>
public sealed record IndexJobStatus
{
    /// <summary>The snapshot returned before any job has ever started.</summary>
    public static readonly IndexJobStatus Idle = new();

    /// <summary>Lifecycle state of the job.</summary>
    public IndexJobState State { get; init; } = IndexJobState.Idle;

    /// <summary>Vector-store collection being indexed; empty when <see cref="State"/> is <see cref="IndexJobState.Idle"/>.</summary>
    public string Collection { get; init; } = string.Empty;

    /// <summary>Local path or repo URL being indexed; empty when idle.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Files indexed so far (or in total once done).</summary>
    public int FilesIndexed { get; init; }

    /// <summary>Files skipped so far (unsupported/empty/unreadable).</summary>
    public int FilesSkipped { get; init; }

    /// <summary>Total matching files counted upfront; null until counted.</summary>
    public int? TotalFiles { get; init; }

    /// <summary>Chunks upserted by the run; populated on completion.</summary>
    public int ChunksIndexed { get; init; }

    /// <summary>When the run started; null while idle.</summary>
    public DateTime? StartedAt { get; init; }

    /// <summary>When the run finished; null while running or idle.</summary>
    public DateTime? FinishedAt { get; init; }

    /// <summary>Failure message; set only when <see cref="State"/> is <see cref="IndexJobState.Failed"/>.</summary>
    public string? Error { get; init; }
}
