using MongoDB.Bson;
using MongoDB.Driver;

namespace Synth.Api.Configuration;

// Mongo-backed IConfigStore, used when a Mongo connection is configured (the
// "synthdata" resource wired in SYNTH-3, renamed from "synthconfig" once it started
// holding more than config). The whole document is stored as a
// single raw JSON string field in one collection document — same reasoning as
// Sonar: Mongo forbids dots in field names, so we never expand the JSON into
// nested Bson. If Mongo is unreachable, LoadAsync degrades to null instead of
// hard-failing startup.
public sealed class MongoConfigStore : IConfigStore
{
    private const string CollectionName = "config";
    private const string DocumentId = "synth";
    private const string JsonField = "Json";

    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoConfigStore(IMongoDatabase database) =>
        _collection = database.GetCollection<BsonDocument>(CollectionName);

    public event Action? Changed;

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var filter = Builders<BsonDocument>.Filter.Eq("_id", DocumentId);
            var document = await _collection.Find(filter).FirstOrDefaultAsync(cancellationToken);

            if (document is null)
                return null;

            return document.TryGetValue(JsonField, out var value) && !value.IsBsonNull
                ? value.AsString
                : null;
        }
        catch (Exception)
        {
            // Mongo may be unreachable (no Docker in local dev, or the container is
            // still starting). Treat that as an empty document rather than crashing.
            return null;
        }
    }

    public async Task SaveAsync(string json, CancellationToken cancellationToken = default)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", DocumentId);
        var update = Builders<BsonDocument>.Update.Set(JsonField, json);

        await _collection.UpdateOneAsync(
            filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken);

        Changed?.Invoke();
    }
}
