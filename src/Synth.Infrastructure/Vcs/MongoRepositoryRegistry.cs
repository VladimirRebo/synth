using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Synth.Domain.Vcs;

namespace Synth.Infrastructure.Vcs;

/// <summary>
/// Mongo-backed <see cref="IRepositoryRegistry"/> storing one document per collection in the
/// <c>repositories</c> collection. Each document is <c>{ _id: &lt;collection&gt;, Json: &lt;entry&gt; }</c>
/// — the entry is kept as a single JSON string field, same as <see cref="Configuration.FileConfigStore"/>
/// (Mongo forbids dots in field names, so we never expand into nested Bson). Reads and writes both
/// swallow connection failures and degrade to empty/no-op so no live Mongo is required in tests/dev.
/// </summary>
public sealed class MongoRepositoryRegistry : IRepositoryRegistry
{
    private const string CollectionName = "repositories";
    private const string JsonField = "Json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoRepositoryRegistry(IMongoDatabase database) =>
        _collection = database.GetCollection<BsonDocument>(CollectionName);

    public async Task UpsertAsync(RepositoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            var json = JsonSerializer.Serialize(entry, JsonOptions);
            var filter = Builders<BsonDocument>.Filter.Eq("_id", entry.Collection);
            var update = Builders<BsonDocument>.Update.Set(JsonField, json);

            await _collection.UpdateOneAsync(
                filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken);
        }
        catch (Exception)
        {
            // Mongo may be unreachable (no Docker in local dev, or still starting). Don't take
            // down the index run over a bookkeeping write — mirror MongoConfigStore's degrade.
        }
    }

    public async Task<bool> DeleteAsync(string collection, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        try
        {
            var filter = Builders<BsonDocument>.Filter.Eq("_id", collection);
            var result = await _collection.DeleteOneAsync(filter, cancellationToken);
            return result.DeletedCount > 0;
        }
        catch (Exception)
        {
            // Mongo unreachable: mirror the other members' graceful degrade. We can't confirm a
            // deletion happened, so report "nothing removed" — the endpoint reads that as a 404.
            return false;
        }
    }

    public async Task<IReadOnlyList<RepositoryEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var documents = await _collection
                .Find(Builders<BsonDocument>.Filter.Empty)
                .ToListAsync(cancellationToken);

            var entries = new List<RepositoryEntry>(documents.Count);
            foreach (var document in documents)
            {
                if (!document.TryGetValue(JsonField, out var value) || value.IsBsonNull)
                    continue;

                var entry = JsonSerializer.Deserialize<RepositoryEntry>(value.AsString, JsonOptions);
                if (entry is not null)
                    entries.Add(entry);
            }

            return entries;
        }
        catch (Exception)
        {
            // Mongo unreachable: an empty list is a safe, non-fatal answer.
            return [];
        }
    }
}
