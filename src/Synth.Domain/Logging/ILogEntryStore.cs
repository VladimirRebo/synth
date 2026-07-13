namespace Synth.Domain.Logging;

/// <summary>
/// Storage behind the live log view (SYNTH-24's <c>GET /logs</c>). Two implementations exist, matching
/// how every other store in this repo pairs a durable local backend with a process-local fallback
/// (<c>IConfigStore</c>, <c>IRepositoryRegistry</c>, <c>ICodeGraphStore</c>): a durable
/// <c>SqliteLogEntryStore</c> (backed by the shared <c>~/.synth/synth.db</c> file, per issue #80) and
/// a process-local <see cref="InMemoryLogEntryStore"/> kept for tests. Entries arrive off the request
/// hot path via <see cref="LogEntryStoreSink"/> + <see cref="LogEntryStoreWriter"/>, so both methods
/// are async.
/// </summary>
public interface ILogEntryStore
{
    /// <summary>Persists a single log entry. Failures must never take down the app — logging is a
    /// side channel — and no external database is required in tests/dev (SQLite is embedded).</summary>
    Task RecordAsync(LogEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent entries, oldest first — the same ordering
    /// <c>RingBufferLogSink.Snapshot()</c> used to return, which <c>LogsController</c>'s
    /// level/since/search filtering depends on. Capped to a client-facing read size (see
    /// <see cref="InMemoryLogEntryStore"/>'s capacity) even when far more history is retained on disk.
    /// </summary>
    Task<IReadOnlyList<LogEntry>> SnapshotAsync(CancellationToken ct = default);
}
