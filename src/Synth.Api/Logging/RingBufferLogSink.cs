using Serilog.Core;
using Serilog.Events;

namespace Synth.Api.Logging;

/// <summary>
/// A bounded, in-memory Serilog sink that keeps the most recent <c>capacity</c> log events
/// queryable in-process — the live "what's happening right now" view behind SYNTH-24's REST
/// endpoint. This is deliberately not durable: no file, no database. Once full, the oldest
/// entry is evicted as each new one arrives.
/// </summary>
/// <remarks>
/// A plain <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/> has no capacity cap,
/// so we guard a <see cref="Queue{T}"/> with a lock: writes enqueue + trim, reads copy the whole
/// queue. Both happen under the same lock, so a reader never observes a half-mutated buffer.
/// </remarks>
public sealed class RingBufferLogSink : ILogEventSink
{
    private const int DefaultCapacity = 1000;

    private readonly int _capacity;
    private readonly Queue<LogEntry> _entries;
    private readonly object _gate = new();

    public RingBufferLogSink(int capacity = DefaultCapacity)
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

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var entry = new LogEntry(
            logEvent.Timestamp,
            logEvent.Level.ToString(),
            logEvent.RenderMessage(),
            logEvent.Exception?.ToString());

        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
            }
        }
    }

    /// <summary>
    /// Returns a stable copy of the currently retained entries, oldest first. The returned list is
    /// detached from the buffer, so a concurrent <see cref="Emit"/> can never mutate it mid-read.
    /// </summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }
}
