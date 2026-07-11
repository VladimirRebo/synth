using System.Text.Json.Serialization;

namespace Synth.Api.Embeddings;

/// <summary>
/// Lifecycle state of the single tracked Ollama model pull. Exactly one "current or most recently
/// finished" pull exists process-wide (Synth is a personal, single-user tool — no queue or history),
/// mirroring <c>IndexJobState</c>.
/// </summary>
public enum OllamaPullState
{
    /// <summary>No pull has ever been started in this process.</summary>
    Idle,

    /// <summary>A model pull is currently in progress.</summary>
    Running,

    /// <summary>The most recent pull completed successfully.</summary>
    Done,

    /// <summary>The most recent pull threw and was recorded as failed.</summary>
    Failed,
}

/// <summary>
/// Immutable snapshot of the current/most-recent Ollama model pull, surfaced so the client can poll
/// progress instead of holding the <c>POST /settings/embedding/ollama/pull</c> response open — the same
/// fire-and-forget + polling shape as <c>IndexJobStatus</c>. Ephemeral, process-lifetime state, never
/// persisted. When nothing has run the snapshot is <see cref="Idle"/> with no other fields populated.
/// </summary>
public sealed record OllamaPullStatus
{
    /// <summary>The snapshot returned before any pull has ever started.</summary>
    public static readonly OllamaPullStatus Idle = new();

    /// <summary>Lifecycle state of the pull.</summary>
    [JsonPropertyName("state")]
    public OllamaPullState State { get; init; } = OllamaPullState.Idle;

    /// <summary>Model being pulled; empty when <see cref="State"/> is <see cref="OllamaPullState.Idle"/>.</summary>
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable progress line from Ollama's pull stream (e.g. <c>"pulling manifest"</c> or
    /// <c>"downloading (42%)"</c>). Empty until the first progress line arrives.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>Failure message; set only when <see cref="State"/> is <see cref="OllamaPullState.Failed"/>.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>
/// Tracks the single current-or-most-recent Ollama model pull so the client can poll progress instead
/// of streaming. Deliberately mirrors <c>IIndexJobTracker</c>'s shape (<see cref="TryStart"/>/
/// <see cref="ReportProgress"/>/<see cref="Complete"/>/<see cref="Fail"/>) — one global pull at a time
/// is enough for a single-user tool. In-memory only; there is no persistence.
/// </summary>
public interface IOllamaPullTracker
{
    /// <summary>The current/most-recent pull; <see cref="OllamaPullStatus.Idle"/> when nothing has run.</summary>
    OllamaPullStatus Current { get; }

    /// <summary>
    /// Atomically transitions to <see cref="OllamaPullState.Running"/> if no pull is already running,
    /// returning <c>true</c>. If one is in progress, leaves it untouched and returns <c>false</c> (the
    /// endpoint maps that to 409).
    /// </summary>
    bool TryStart(string model);

    /// <summary>Updates the in-flight pull's human-readable status line. No-op if no pull is running.</summary>
    void ReportProgress(string status);

    /// <summary>Transitions the pull to <see cref="OllamaPullState.Done"/>.</summary>
    void Complete();

    /// <summary>Transitions the pull to <see cref="OllamaPullState.Failed"/> with an error message.</summary>
    void Fail(string error);
}

/// <summary>
/// Process-local <see cref="IOllamaPullTracker"/> guarding a single mutable status behind a lock —
/// updates are infrequent (one per streamed pull line), so a plain lock is enough. Registered as a
/// DI singleton, exactly like <c>InMemoryIndexJobTracker</c>.
/// </summary>
public sealed class InMemoryOllamaPullTracker : IOllamaPullTracker
{
    private readonly object _gate = new();
    private OllamaPullStatus _current = OllamaPullStatus.Idle;

    public OllamaPullStatus Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public bool TryStart(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        lock (_gate)
        {
            if (_current.State == OllamaPullState.Running)
                return false;

            _current = new OllamaPullStatus
            {
                State = OllamaPullState.Running,
                Model = model,
            };
            return true;
        }
    }

    public void ReportProgress(string status)
    {
        lock (_gate)
        {
            if (_current.State != OllamaPullState.Running)
                return;

            _current = _current with { Status = status };
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _current = _current with
            {
                State = OllamaPullState.Done,
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
                State = OllamaPullState.Failed,
                Error = error,
            };
        }
    }
}
