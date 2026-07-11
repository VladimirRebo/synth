using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Synth.Domain.Logging;

namespace Synth.Api.Logging;

/// <summary>
/// Durable <see cref="ILogEntryStore"/> backed by a <em>capped</em> Mongo collection — the same shape
/// Sonar's own audit log uses (concept <c>rbac-audit-pattern</c>): fixed size by document count and
/// bytes, oldest entries self-evict, no separate retention job, ordered by <c>$natural</c> (insertion
/// order) rather than an index. A single Synth instance emits many log lines per request, so entries
/// are inserted one document at a time off the request hot path (see <see cref="LogEntryStoreSink"/>).
/// Reads and writes swallow connection failures and degrade to empty/no-op, so no live Mongo is
/// required in tests/dev — the same guarantee as every other Mongo-backed piece in this repo.
/// </summary>
public sealed class MongoLogEntryStore : ILogEntryStore
{
    private const string CollectionName = "logs";

    // Sonar's documented auth_audit sizing: a capped collection bounded by both bytes and document
    // count, whichever is hit first.
    private const long MaxSizeBytes = 16 * 1024 * 1024;
    private const long MaxDocuments = 20_000;

    // Client-facing read size. Far more history stays on disk (up to MaxDocuments), but a snapshot
    // returns only the most recent slice so GET /logs behaves exactly as it did over the ring buffer.
    private const int ReadLimit = InMemoryLogEntryStore.DefaultCapacity;

    private readonly IMongoCollection<LogEntryDocument> _collection;

    public MongoLogEntryStore(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        try
        {
            // The collection must be created capped before its first insert; creating it later or
            // letting an insert auto-create it would give an ordinary uncapped collection. Creation
            // fails once the collection already exists, which is the normal steady state — swallow
            // that (and any "Mongo unreachable") so construction never hard-fails.
            database.CreateCollection(CollectionName, new CreateCollectionOptions
            {
                Capped = true,
                MaxSize = MaxSizeBytes,
                MaxDocuments = MaxDocuments,
            });
        }
        catch (Exception)
        {
            // Already-capped collection or unreachable Mongo — both non-fatal here.
        }

        _collection = database.GetCollection<LogEntryDocument>(CollectionName);
    }

    public async Task RecordAsync(LogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            await _collection.InsertOneAsync(LogEntryDocument.From(entry), cancellationToken: ct);
        }
        catch (Exception)
        {
            // Mongo unreachable: a dropped log line must never crash the drain loop or the app.
        }
    }

    public async Task<IReadOnlyList<LogEntry>> SnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            // $natural descending = newest first; take the most recent ReadLimit, then reverse to the
            // oldest-first order LogsEndpoints (and the in-memory store) expose.
            var documents = await _collection
                .Find(FilterDefinition<LogEntryDocument>.Empty)
                .Sort(Builders<LogEntryDocument>.Sort.Descending("$natural"))
                .Limit(ReadLimit)
                .ToListAsync(ct);

            documents.Reverse();
            return documents.Select(d => d.ToEntry()).ToList();
        }
        catch (Exception)
        {
            // Mongo unreachable: an empty list is a safe, non-fatal answer.
            return [];
        }
    }

    // BSON document shape for a log entry. A generated ObjectId _id keeps each entry a distinct
    // document; insertion order (which the capped collection preserves) is what SnapshotAsync sorts on.
    private sealed class LogEntryDocument
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        public required DateTimeOffset Timestamp { get; set; }
        public required string Level { get; set; }
        public required string Message { get; set; }
        public string? Exception { get; set; }

        public static LogEntryDocument From(LogEntry entry) => new()
        {
            Timestamp = entry.Timestamp,
            Level = entry.Level,
            Message = entry.Message,
            Exception = entry.Exception,
        };

        public LogEntry ToEntry() => new(Timestamp, Level, Message, Exception);
    }
}
