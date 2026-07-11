using System.Threading.Channels;
using Serilog.Core;
using Serilog.Events;
using Synth.Domain.Logging;

namespace Synth.Api.Logging;

/// <summary>
/// The Serilog sink Synth's logging pipeline writes into. Converts each <see cref="LogEvent"/> to a
/// <see cref="LogEntry"/> and hands it to a bounded in-memory channel — nothing more. The actual
/// persistence (Mongo or in-memory) happens on <see cref="LogEntryStoreWriter"/>'s background thread
/// draining that channel, so <see cref="Emit"/> stays fast and non-blocking and never adds I/O latency
/// to the request hot path (a single request can emit many log lines).
/// </summary>
/// <remarks>
/// Overflow policy: <see cref="BoundedChannelFullMode.DropOldest"/> — this is a live tail, not a
/// guaranteed-delivery log shipper, so when the drain falls behind we drop the oldest queued entries
/// and keep the newest. <see cref="Emit"/>'s <c>TryWrite</c> therefore always succeeds without
/// blocking.
/// </remarks>
public sealed class LogEntryStoreSink : ILogEventSink
{
    // Headroom for bursts while the background writer drains to the store. Oldest entries are dropped
    // if this fills, which only loses a few lines from the live tail under sustained overload.
    private const int DefaultQueueCapacity = 10_000;

    private readonly Channel<LogEntry> _channel;

    public LogEntryStoreSink(int queueCapacity = DefaultQueueCapacity)
    {
        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity), queueCapacity, "Capacity must be positive.");
        }

        _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>The stream of captured entries, drained by <see cref="LogEntryStoreWriter"/>.</summary>
    public ChannelReader<LogEntry> Reader => _channel.Reader;

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        // The exact conversion RingBufferLogSink.Emit did — rendered message, level name, and the
        // exception's ToString() when present.
        var entry = new LogEntry(
            logEvent.Timestamp,
            logEvent.Level.ToString(),
            logEvent.RenderMessage(),
            logEvent.Exception?.ToString());

        // DropOldest means TryWrite never blocks and never fails on a full channel: it evicts the
        // oldest queued entry to make room. Keeps Emit off the I/O path entirely.
        _channel.Writer.TryWrite(entry);
    }
}
