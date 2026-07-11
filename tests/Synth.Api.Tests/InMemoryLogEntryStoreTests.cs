using Synth.Api.Logging;
using Synth.Domain.Logging;

namespace Synth.Api.Tests;

// Round-trip + capacity-eviction coverage of the ILogEntryStore contract against the in-memory
// implementation (the production fallback used when no Mongo is configured, and the same shape
// MongoLogEntryStore exposes). Supersedes RingBufferLogSinkTests: the ring-buffer logic moved into
// InMemoryLogEntryStore, and the LogEvent -> LogEntry conversion moved into LogEntryStoreSink
// (covered by LogEntryStoreSinkTests). No live Mongo required — matching how this repo tests its
// Mongo-backed stores (see RepositoryRegistryTests / CodeGraphStoreTests).
public class InMemoryLogEntryStoreTests
{
    private static LogEntry Entry(int minute, string level = "Information", string? message = null) =>
        new(
            new DateTimeOffset(2026, 1, 1, 12, minute, 0, TimeSpan.Zero),
            level,
            message ?? $"entry {minute}",
            Exception: null);

    [Fact]
    public async Task Record_then_snapshot_round_trips_oldest_first()
    {
        var store = new InMemoryLogEntryStore(capacity: 10);

        await store.RecordAsync(Entry(0, message: "first"));
        await store.RecordAsync(Entry(1, message: "second"));

        var snapshot = await store.SnapshotAsync();

        Assert.Equal(new[] { "first", "second" }, snapshot.Select(e => e.Message));
    }

    [Fact]
    public async Task Evicts_oldest_entries_once_at_capacity()
    {
        const int capacity = 5;
        var store = new InMemoryLogEntryStore(capacity);

        for (var i = 0; i < capacity * 3; i++)
        {
            await store.RecordAsync(Entry(i, message: $"entry {i}"));
        }

        var snapshot = await store.SnapshotAsync();

        // Only the most recent `capacity` entries survive, in arrival order (oldest first).
        Assert.Equal(capacity, snapshot.Count);
        Assert.Equal("entry 10", snapshot[0].Message);
        Assert.Equal("entry 14", snapshot[^1].Message);
    }

    [Fact]
    public async Task Snapshot_returns_a_detached_copy()
    {
        var store = new InMemoryLogEntryStore(capacity: 10);

        await store.RecordAsync(Entry(0, message: "first"));
        var before = await store.SnapshotAsync();

        await store.RecordAsync(Entry(1, message: "second"));
        var after = await store.SnapshotAsync();

        // The earlier snapshot is unaffected by the later write.
        Assert.Single(before);
        Assert.Equal(2, after.Count);
    }

    [Fact]
    public async Task Snapshot_is_empty_before_any_record()
    {
        var store = new InMemoryLogEntryStore();

        Assert.Empty(await store.SnapshotAsync());
    }

    [Fact]
    public async Task Concurrent_writes_never_exceed_capacity_and_stay_consistent()
    {
        const int capacity = 100;
        const int tasks = 8;
        const int perTask = 500;
        var store = new InMemoryLogEntryStore(capacity);

        var workers = Enumerable.Range(0, tasks).Select(t => Task.Run(async () =>
        {
            for (var i = 0; i < perTask; i++)
            {
                await store.RecordAsync(Entry(0, message: $"t{t}-{i}"));
            }
        }));

        await Task.WhenAll(workers);

        var snapshot = await store.SnapshotAsync();

        // The buffer is capped regardless of contention, and every retained slot is a real,
        // fully-formed entry (no torn writes / nulls leaking through).
        Assert.Equal(capacity, snapshot.Count);
        Assert.All(snapshot, e => Assert.StartsWith("t", e.Message));
    }

    [Fact]
    public void Rejects_non_positive_capacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryLogEntryStore(capacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryLogEntryStore(capacity: -1));
    }
}
