using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Synth.Domain.Graph;

namespace Synth.Infrastructure.Graph;

/// <summary>
/// Mongo-backed <see cref="ICodeGraphStore"/>. Unlike <c>MongoRepositoryRegistry</c>/
/// <c>MongoConfigStore</c> — which store one opaque JSON blob per key because they only ever fetch by
/// key — the call graph must filter on <see cref="CallEdge.Caller"/> and <see cref="CallEdge.Callee"/>
/// in <em>both</em> directions, so each edge is a real BSON document with real fields, backed by
/// compound indexes on <c>(Collection, Caller)</c> and <c>(Collection, Callee)</c>. Reads and writes
/// swallow connection failures and degrade to empty/no-op, so no live Mongo is required in tests/dev —
/// the same guarantee as every other Mongo-backed piece in this repo.
/// </summary>
public sealed class MongoCodeGraphStore : ICodeGraphStore
{
    private const string CollectionName = "call_edges";

    private readonly IMongoCollection<CallEdgeDocument> _collection;

    public MongoCodeGraphStore(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _collection = database.GetCollection<CallEdgeDocument>(CollectionName);

        try
        {
            // Both directions of the graph query filter by (Collection, Caller/Callee). Index
            // creation is idempotent, so re-running on every construction is safe and cheap.
            _collection.Indexes.CreateMany(
            [
                new CreateIndexModel<CallEdgeDocument>(Builders<CallEdgeDocument>.IndexKeys
                    .Ascending(d => d.Collection).Ascending(d => d.Caller)),
                new CreateIndexModel<CallEdgeDocument>(Builders<CallEdgeDocument>.IndexKeys
                    .Ascending(d => d.Collection).Ascending(d => d.Callee)),
            ]);
        }
        catch (Exception)
        {
            // Mongo may be unreachable (no Docker in local dev, or still starting). Index creation
            // is an optimization, not a correctness requirement — never fail construction over it.
        }
    }

    public async Task ReplaceEdgesAsync(string collection, IReadOnlyList<CallEdge> edges, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(edges);

        try
        {
            // Delete-then-insert: a full replace so a re-index never leaves stale edges behind.
            var filter = Builders<CallEdgeDocument>.Filter.Eq(d => d.Collection, collection);
            await _collection.DeleteManyAsync(filter, ct);

            if (edges.Count > 0)
                await _collection.InsertManyAsync(edges.Select(CallEdgeDocument.From), cancellationToken: ct);
        }
        catch (Exception)
        {
            // Mongo unreachable: don't take down the index run over a graph write — degrade to no-op,
            // mirroring MongoRepositoryRegistry.
        }
    }

    public Task<IReadOnlyList<CallEdge>> FindCallersAsync(string collection, string symbol, CancellationToken ct = default) =>
        FindAsync(Builders<CallEdgeDocument>.Filter.And(
            Builders<CallEdgeDocument>.Filter.Eq(d => d.Collection, collection),
            Builders<CallEdgeDocument>.Filter.Eq(d => d.Callee, symbol)), ct);

    public Task<IReadOnlyList<CallEdge>> FindCalleesAsync(string collection, string symbol, CancellationToken ct = default) =>
        FindAsync(Builders<CallEdgeDocument>.Filter.And(
            Builders<CallEdgeDocument>.Filter.Eq(d => d.Collection, collection),
            Builders<CallEdgeDocument>.Filter.Eq(d => d.Caller, symbol)), ct);

    private async Task<IReadOnlyList<CallEdge>> FindAsync(FilterDefinition<CallEdgeDocument> filter, CancellationToken ct)
    {
        try
        {
            var documents = await _collection.Find(filter).ToListAsync(ct);
            return documents.Select(d => d.ToEdge()).ToList();
        }
        catch (Exception)
        {
            // Mongo unreachable: an empty list is a safe, non-fatal answer.
            return [];
        }
    }

    // BSON document shape for a call edge. A generated ObjectId _id keeps each edge a distinct
    // document (unlike the key-addressed stores) so InsertMany/DeleteMany work as a bulk swap.
    private sealed class CallEdgeDocument
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        public required string Collection { get; set; }
        public required string Caller { get; set; }
        public required string Callee { get; set; }
        public required string SourceFile { get; set; }
        public required int Line { get; set; }

        public static CallEdgeDocument From(CallEdge edge) => new()
        {
            Collection = edge.Collection,
            Caller = edge.Caller,
            Callee = edge.Callee,
            SourceFile = edge.SourceFile,
            Line = edge.Line,
        };

        public CallEdge ToEdge() => new(Collection, Caller, Callee, SourceFile, Line);
    }
}
