using Synth.Domain.Logging;
namespace Synth.Infrastructure.Logging;

/// <summary>
/// Bounded, process-local <see cref="ILogEntryStore"/> — the fallback used when no Mongo connection
/// is configured, and the "no live database required" guarantee tests and Docker-less local dev run
/// on (the same role <c>InMemoryRepositoryRegistry</c> plays for <c>MongoRepositoryRegistry</c>).
/// Holds the most recent <see cref="Capacity"/> entries; once full, the oldest is evicted as each new
/// one arrives. This is the ring-buffer logic that used to live in <c>RingBufferLogSink</c>, now
/// behind the store abstraction.
/// </summary>
/// <remarks>
/// A plain <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/> has no capacity cap, so we
/// guard a <see cref="Queue{T}"/> with a lock: writes enqueue + trim, reads copy the whole queue.
/// Both happen under the same lock, so a reader never observes a half-mutated buffer.
/// </remarks>
public sealed class InMemoryLogEntryStore : ILogEntryStore
{
    internal const int DefaultCapacity = 1000;

    private readonly int _capacity;
    private readonly Queue<LogEntry> _entries;
    private readonly object _gate = new();

    public InMemoryLogEntryStore(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        _capacity = capacity;
        _entries = new Queue<LogEntry>(capacity);
    }

    /// <summary>The configured maximum number of retained entries.</summary>
    public int Capacity => _capacity;

    public Task RecordAsync(LogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns a stable copy of the currently retained entries, oldest first. The returned list is
    /// detached from the buffer, so a concurrent <see cref="RecordAsync"/> can never mutate it mid-read.
    /// </summary>
    public Task<IReadOnlyList<LogEntry>> SnapshotAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<LogEntry>>(_entries.ToArray());
        }
    }
}
