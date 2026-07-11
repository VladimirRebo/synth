namespace Synth.Domain.Logging;

/// <summary>
/// Storage behind the live log view (SYNTH-24's <c>GET /logs</c>). Two implementations follow the
/// same "Mongo when configured, in-memory fallback otherwise" duality every other store in this repo
/// uses (<c>IConfigStore</c>, <c>IRepositoryRegistry</c>, <c>ICodeGraphStore</c>): a durable
/// <see cref="MongoLogEntryStore"/> (a capped collection) and a process-local
/// <see cref="InMemoryLogEntryStore"/>. Entries arrive off the request hot path via
/// <see cref="LogEntryStoreSink"/> + <see cref="LogEntryStoreWriter"/>, so both methods are async.
/// </summary>
public interface ILogEntryStore
{
    /// <summary>Persists a single log entry. Failures degrade to a no-op — logging must never
    /// take down the app, and no live Mongo is required in tests/dev.</summary>
    Task RecordAsync(LogEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent entries, oldest first — the same ordering
    /// <c>RingBufferLogSink.Snapshot()</c> used to return, which <see cref="LogsEndpoints"/>'s
    /// level/since/search filtering depends on. Capped to a client-facing read size (see
    /// <see cref="InMemoryLogEntryStore"/>'s capacity) even when far more history is retained on disk.
    /// </summary>
    Task<IReadOnlyList<LogEntry>> SnapshotAsync(CancellationToken ct = default);
}
