using MongoDB.Driver;
using Synth.Api.Logging;

namespace Synth.Api.Tests;

// MongoLogEntryStore must degrade gracefully when Mongo is unreachable — the same "no live Mongo
// required in tests/dev" guarantee every other Mongo-backed store in this repo makes. Its durability
// contract (a fresh instance still sees previously recorded entries) is a property of the capped
// Mongo collection itself and is exercised only against a live server in integration, never here —
// matching how MongoRepositoryRegistry / MongoCodeGraphStore are tested (see CodeGraphStoreTests).
public class MongoLogEntryStoreTests
{
    // A client pointed at a port nothing is listening on, with a short server-selection timeout so
    // constructing the store (which eagerly tries to create the capped collection) fails fast and is
    // swallowed rather than hanging on the 30s default.
    private static MongoLogEntryStore UnreachableStore()
    {
        var settings = MongoClientSettings.FromConnectionString("mongodb://localhost:1/synthdata");
        settings.ServerSelectionTimeout = TimeSpan.FromMilliseconds(200);
        var database = new MongoClient(settings).GetDatabase("synthdata");
        return new MongoLogEntryStore(database);
    }

    [Fact]
    public async Task Record_does_not_throw_when_mongo_is_unreachable()
    {
        var store = UnreachableStore();

        // A swallowed failure, not an exception bubbling into the background drain loop.
        await store.RecordAsync(new LogEntry(DateTimeOffset.UtcNow, "Information", "hello", Exception: null));
    }

    [Fact]
    public async Task Snapshot_is_empty_when_mongo_is_unreachable()
    {
        var store = UnreachableStore();

        Assert.Empty(await store.SnapshotAsync());
    }
}
