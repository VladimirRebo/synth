using Serilog;
using Serilog.Events;
using Synth.Infrastructure.Logging;
using Synth.Domain.Logging;

namespace Synth.Infrastructure.Tests;

// The LogEvent -> LogEntry conversion that used to live in RingBufferLogSink.Emit now lives in
// LogEntryStoreSink, which drops entries onto a channel instead of a ring buffer. These assert the
// conversion is unchanged (level name, rendered message, exception ToString) and that Emit stays
// non-blocking by handing entries to the channel a background writer drains.
public class LogEntryStoreSinkTests
{
    // Builds a Serilog logger that writes into the given sink, at the lowest level so every event
    // flows through — mirrors how Program.cs pipes events into the same instance.
    private static Serilog.ILogger BuildLogger(LogEntryStoreSink sink) =>
        new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

    // Drains everything currently buffered on the sink's channel, oldest first.
    private static List<LogEntry> Drain(LogEntryStoreSink sink)
    {
        var drained = new List<LogEntry>();
        while (sink.Reader.TryRead(out var entry))
            drained.Add(entry);
        return drained;
    }

    [Fact]
    public void Captures_events_at_their_level_with_rendered_message()
    {
        var sink = new LogEntryStoreSink();
        var logger = BuildLogger(sink);

        logger.Information("hello {Name}", "world");
        logger.Warning("careful");
        logger.Error("boom {Code}", 42);

        var entries = Drain(sink);

        Assert.Equal(3, entries.Count);
        Assert.Equal(LogEventLevel.Information.ToString(), entries[0].Level);
        Assert.Equal("hello \"world\"", entries[0].Message);
        Assert.Equal(LogEventLevel.Warning.ToString(), entries[1].Level);
        Assert.Equal("careful", entries[1].Message);
        Assert.Equal(LogEventLevel.Error.ToString(), entries[2].Level);
        Assert.Equal("boom 42", entries[2].Message);
    }

    [Fact]
    public void Captures_exception_details_when_present()
    {
        var sink = new LogEntryStoreSink();
        var logger = BuildLogger(sink);

        logger.Information("no exception here");
        logger.Error(new InvalidOperationException("kaboom"), "it failed");

        var entries = Drain(sink);

        Assert.Null(entries[0].Exception);
        Assert.NotNull(entries[1].Exception);
        Assert.Contains("InvalidOperationException", entries[1].Exception);
        Assert.Contains("kaboom", entries[1].Exception);
    }

    [Fact]
    public void Emit_drops_oldest_when_the_channel_is_full()
    {
        // A tiny channel makes overflow deterministic: the drop-oldest policy keeps the newest N.
        var sink = new LogEntryStoreSink(queueCapacity: 3);
        var logger = BuildLogger(sink);

        for (var i = 0; i < 6; i++)
            logger.Information("entry {Index}", i);

        var entries = Drain(sink);

        // Capacity 3, oldest evicted: entries 3, 4, 5 remain in order.
        Assert.Equal(new[] { "entry 3", "entry 4", "entry 5" }, entries.Select(e => e.Message));
    }

    [Fact]
    public void Rejects_non_positive_capacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogEntryStoreSink(queueCapacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogEntryStoreSink(queueCapacity: -1));
    }
}
