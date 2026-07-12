using Qdrant.Client;
using Synth.Domain;
using Synth.Infrastructure.Storage;

namespace Synth.Infrastructure.Tests.Storage;

// Exercises QdrantCodeChunkStore against a *live* Qdrant instance. There is no in-memory Qdrant to
// stand in — the dimension guard is a property of the real gRPC collection metadata — so, following
// the same "no live backend required in tests/dev" stance the Mongo-backed stores take
// (MongoLogEntryStoreTests / CodeGraphStoreTests), this test runs only when a Qdrant endpoint is
// configured via the SYNTH_TEST_QDRANT_URL environment variable and no-ops (graceful skip) otherwise.
// Point it at a throwaway Qdrant (e.g. `docker run -p 6334:6334 qdrant/qdrant`) with:
//   SYNTH_TEST_QDRANT_URL=http://localhost:6334 [SYNTH_TEST_QDRANT_KEY=<key>] dotnet test
public class QdrantCodeChunkStoreTests
{
    private const string UrlVariable = "SYNTH_TEST_QDRANT_URL";
    private const string KeyVariable = "SYNTH_TEST_QDRANT_KEY";

    [Fact]
    public async Task Upsert_into_existing_collection_with_a_different_dimension_throws_DimensionMismatch()
    {
        var url = Environment.GetEnvironmentVariable(UrlVariable);
        if (string.IsNullOrWhiteSpace(url))
            return; // No live Qdrant configured — skip gracefully, like the Mongo store tests do.

        var apiKey = Environment.GetEnvironmentVariable(KeyVariable);
        using var client = new QdrantClient(new Uri(url), apiKey);
        var store = new QdrantCodeChunkStore(client);

        // Unique per run so repeated runs never collide, and so a crashed prior run can't poison this
        // one. Sanitized name is lowercase hex + '-', which SanitizeCollectionName passes through as-is.
        var collection = $"synth-dim-test-{Guid.NewGuid():N}";

        try
        {
            // First upsert creates the collection at 4 dimensions.
            await store.UpsertAsync(collection, [ChunkWithVector("A", [0.1f, 0.2f, 0.3f, 0.4f])]);

            // Second upsert into the same collection with a different (3-dim) vector must be rejected
            // up front with a clear DimensionMismatchException, not Qdrant's raw gRPC "expected dim" error.
            var mismatch = await Assert.ThrowsAsync<DimensionMismatchException>(() =>
                store.UpsertAsync(collection, [ChunkWithVector("B", [0.5f, 0.6f, 0.7f])]));

            Assert.Equal(collection, mismatch.Collection);
            Assert.Equal(4, mismatch.ExpectedDimension);
            Assert.Equal(3, mismatch.ActualDimension);
        }
        finally
        {
            await client.DeleteCollectionAsync(collection);
        }
    }

    [Fact]
    public async Task GetBySymbolAsync_filters_by_class_and_method_with_AND()
    {
        var url = Environment.GetEnvironmentVariable(UrlVariable);
        if (string.IsNullOrWhiteSpace(url))
            return; // No live Qdrant configured — skip gracefully, like the Mongo store tests do.

        var apiKey = Environment.GetEnvironmentVariable(KeyVariable);
        using var client = new QdrantClient(new Uri(url), apiKey);
        var store = new QdrantCodeChunkStore(client);

        var collection = $"synth-symbol-test-{Guid.NewGuid():N}";

        try
        {
            await store.UpsertAsync(collection,
            [
                SymbolChunk("user.cs", "UserRepository", "GetById", [0.1f, 0.2f]),
                SymbolChunk("user.cs", "UserRepository", "Save", [0.3f, 0.4f]),
                SymbolChunk("order.cs", "OrderRepository", "GetById", [0.5f, 0.6f]),
            ]);

            // Class-only: both UserRepository methods.
            var byClass = await store.GetBySymbolAsync(collection, "UserRepository", methodName: null);
            Assert.Equal(["GetById", "Save"], byClass.Select(c => c.MethodName));

            // Method-only across classes.
            var byMethod = await store.GetBySymbolAsync(collection, className: null, methodName: "GetById");
            Assert.Equal(["OrderRepository", "UserRepository"], byMethod.Select(c => c.ClassName));

            // Both combined with AND narrows to the single chunk.
            var byBoth = await store.GetBySymbolAsync(collection, "UserRepository", "GetById");
            var only = Assert.Single(byBoth);
            Assert.Equal("UserRepository", only.ClassName);
            Assert.Equal("GetById", only.MethodName);

            // No match yields empty.
            Assert.Empty(await store.GetBySymbolAsync(collection, "MissingClass", methodName: null));
        }
        finally
        {
            await client.DeleteCollectionAsync(collection);
        }
    }

    private static CodeChunk ChunkWithVector(string id, float[] vector) => new()
    {
        RelativePath = $"{id}.cs",
        StartLine = 1,
        EndLine = 2,
        Content = id,
        Embedding = vector,
    };

    private static CodeChunk SymbolChunk(string relativePath, string className, string methodName, float[] vector) => new()
    {
        RelativePath = relativePath,
        ClassName = className,
        MethodName = methodName,
        StartLine = 1,
        EndLine = 2,
        Content = $"{className}.{methodName}",
        Embedding = vector,
    };
}
