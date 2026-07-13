using Synth.Domain.Logging;
using Synth.Infrastructure;
using Synth.Infrastructure.Logging;

namespace Synth.Infrastructure.Tests;

// Round-trip + eviction coverage of SqliteLogEntryStore against a real temp-file SQLite database, so
// we exercise the actual SQL (table creation, insert, oldest-first snapshot, bounded eviction) rather
// than mocks. Each test gets a throwaway db file that is deleted afterward — never touches
// ~/.synth/synth.db. Mirrors SqliteCodeGraphStoreTests'/SqliteRepositoryRegistryTests' style.
public class SqliteLogEntryStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteConnectionFactory _factory;

    public SqliteLogEntryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "synth-tests", Guid.NewGuid().ToString("N"));
        _factory = new SqliteConnectionFactory(Path.Combine(_tempDir, "synth.db"));
    }

    private static LogEntry Entry(int minute, string level = "Information", string? message = null, string? exception = null) =>
        new(
            new DateTimeOffset(2026, 1, 1, 12, minute, 0, TimeSpan.Zero),
            level,
            message ?? $"entry {minute}",
            exception);

    [Fact]
    public async Task Record_then_snapshot_round_trips_oldest_first()
    {
        var store = new SqliteLogEntryStore(_factory);

        await store.RecordAsync(Entry(0, message: "first"));
        await store.RecordAsync(Entry(1, message: "second"));
        await store.RecordAsync(Entry(2, message: "third"));

        var snapshot = await store.SnapshotAsync();

        Assert.Equal(new[] { "first", "second", "third" }, snapshot.Select(e => e.Message));
    }

    [Fact]
    public async Task All_fields_round_trip_through_a_real_file()
    {
        var store = new SqliteLogEntryStore(_factory);
        var entry = new LogEntry(
            new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(2)),
            "Error",
            "boom",
            Exception: "System.InvalidOperationException: nope");
        await store.RecordAsync(entry);

        var found = Assert.Single(await store.SnapshotAsync());
        Assert.Equal(entry, found);
    }

    [Fact]
    public async Task Null_exception_round_trips_as_null()
    {
        var store = new SqliteLogEntryStore(_factory);
        await store.RecordAsync(Entry(0, exception: null));

        var found = Assert.Single(await store.SnapshotAsync());
        Assert.Null(found.Exception);
    }

    [Fact]
    public async Task Snapshot_before_any_record_is_empty_and_creates_the_table()
    {
        var store = new SqliteLogEntryStore(_factory);

        // No writes yet: the first read must create the schema and return nothing rather than throw.
        Assert.Empty(await store.SnapshotAsync());
    }

    [Fact]
    public async Task Eviction_keeps_only_the_newest_max_documents_rows()
    {
        // Small bound + per-insert eviction so the trim logic runs without inserting 20k rows.
        var store = new SqliteLogEntryStore(_factory, maxDocuments: 5, evictionInterval: 1);

        for (var i = 0; i < 20; i++)
        {
            await store.RecordAsync(Entry(i % 60, message: $"entry {i}"));
        }

        var snapshot = await store.SnapshotAsync();

        // Only the most recent 5 survive, oldest-first (entries 15..19).
        Assert.Equal(5, snapshot.Count);
        Assert.Equal(
            new[] { "entry 15", "entry 16", "entry 17", "entry 18", "entry 19" },
            snapshot.Select(e => e.Message));
    }

    [Fact]
    public async Task Eviction_is_batched_so_the_table_can_transiently_exceed_the_bound()
    {
        // With an interval larger than the number of inserts, the trim never fires: every row stays,
        // proving eviction is amortized rather than run on every insert.
        var store = new SqliteLogEntryStore(_factory, maxDocuments: 5, evictionInterval: 100);

        for (var i = 0; i < 20; i++)
        {
            await store.RecordAsync(Entry(i % 60, message: $"entry {i}"));
        }

        var snapshot = await store.SnapshotAsync();

        Assert.Equal(20, snapshot.Count);
    }

    [Fact]
    public async Task Rejects_non_positive_bounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteLogEntryStore(_factory, maxDocuments: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteLogEntryStore(_factory, evictionInterval: 0));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
