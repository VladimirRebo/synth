using Serilog;
using Serilog.Events;
using Synth.Api.Logging;

namespace Synth.Api.Tests;

public class RingBufferLogSinkTests
{
    // Builds a Serilog logger that writes into the given sink, at the lowest level so every
    // event flows through — mirrors how Program.cs pipes events into the same instance.
    private static Serilog.ILogger BuildLogger(RingBufferLogSink sink) =>
        new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

    [Fact]
    public void Captures_events_at_their_level_with_rendered_message()
    {
        var sink = new RingBufferLogSink(capacity: 10);
        var logger = BuildLogger(sink);

        logger.Information("hello {Name}", "world");
        logger.Warning("careful");
        logger.Error("boom {Code}", 42);

        var snapshot = sink.Snapshot();

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(LogEventLevel.Information.ToString(), snapshot[0].Level);
        Assert.Equal("hello \"world\"", snapshot[0].Message);
        Assert.Equal(LogEventLevel.Warning.ToString(), snapshot[1].Level);
        Assert.Equal("careful", snapshot[1].Message);
        Assert.Equal(LogEventLevel.Error.ToString(), snapshot[2].Level);
        Assert.Equal("boom 42", snapshot[2].Message);
    }

    [Fact]
    public void Captures_exception_details_when_present()
    {
        var sink = new RingBufferLogSink(capacity: 10);
        var logger = BuildLogger(sink);

        logger.Information("no exception here");
        logger.Error(new InvalidOperationException("kaboom"), "it failed");

        var snapshot = sink.Snapshot();

        Assert.Null(snapshot[0].Exception);
        Assert.NotNull(snapshot[1].Exception);
        Assert.Contains("InvalidOperationException", snapshot[1].Exception);
        Assert.Contains("kaboom", snapshot[1].Exception);
    }

    [Fact]
    public void Evicts_oldest_entries_once_at_capacity()
    {
        const int capacity = 5;
        var sink = new RingBufferLogSink(capacity);
        var logger = BuildLogger(sink);

        for (var i = 0; i < capacity * 3; i++)
        {
            logger.Information("entry {Index}", i);
        }

        var snapshot = sink.Snapshot();

        // Only the most recent `capacity` entries survive, in arrival order (oldest first).
        Assert.Equal(capacity, snapshot.Count);
        Assert.Equal("entry 10", snapshot[0].Message);
        Assert.Equal("entry 14", snapshot[^1].Message);
    }

    [Fact]
    public void Snapshot_returns_a_detached_copy()
    {
        var sink = new RingBufferLogSink(capacity: 10);
        var logger = BuildLogger(sink);

        logger.Information("first");
        var before = sink.Snapshot();

        logger.Information("second");
        var after = sink.Snapshot();

        // The earlier snapshot is unaffected by the later write.
        Assert.Single(before);
        Assert.Equal(2, after.Count);
    }

    [Fact]
    public async Task Concurrent_writes_never_exceed_capacity_and_stay_consistent()
    {
        const int capacity = 100;
        const int tasks = 8;
        const int perTask = 500;
        var sink = new RingBufferLogSink(capacity);
        var logger = BuildLogger(sink);

        var workers = Enumerable.Range(0, tasks).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < perTask; i++)
            {
                logger.Information("t{Task}-{Index}", t, i);
            }
        }));

        await Task.WhenAll(workers);

        var snapshot = sink.Snapshot();

        // The buffer is capped regardless of contention, and every retained slot is a real,
        // fully-formed entry (no torn writes / nulls leaking through).
        Assert.Equal(capacity, snapshot.Count);
        Assert.All(snapshot, e => Assert.StartsWith("t", e.Message));
        Assert.Equal(capacity, new HashSet<LogEntry>(snapshot).Count);
    }

    [Fact]
    public void Rejects_non_positive_capacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingBufferLogSink(capacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingBufferLogSink(capacity: -1));
    }
}
