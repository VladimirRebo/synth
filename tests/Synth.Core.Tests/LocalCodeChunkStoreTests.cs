using Synth.Core;
using Synth.Domain;

namespace Synth.Core.Tests;

// Proves SYNTH-9 wiring against the in-memory Local store (no Qdrant/Docker): upsert +
// nearest-neighbour search (closer vectors rank higher) + get-by-file, plus upsert-as-replace.
public class LocalCodeChunkStoreTests
{
    private static CodeChunk Chunk(
        string relativePath,
        int startLine,
        float[] embedding,
        string methodName = "M") => new()
    {
        RelativePath = relativePath,
        MethodName = methodName,
        StartLine = startLine,
        EndLine = startLine + 1,
        Content = $"{relativePath}:{startLine}",
        Embedding = embedding,
    };

    [Fact]
    public async Task SearchAsync_ranks_closer_vectors_higher()
    {
        var store = new LocalCodeChunkStore();
        var near = Chunk("a.cs", 1, [1f, 0f, 0f], "Near");
        var mid = Chunk("b.cs", 1, [0.8f, 0.2f, 0f], "Mid");
        var far = Chunk("c.cs", 1, [0f, 1f, 0f], "Far");

        await store.UpsertAsync(CollectionNames.Default, [far, near, mid]);

        var results = await store.SearchAsync(CollectionNames.Default, new[] { 1f, 0f, 0f }, limit: 3);

        Assert.Equal(3, results.Count);
        Assert.Equal("Near", results[0].Chunk.MethodName);
        Assert.Equal("Mid", results[1].Chunk.MethodName);
        Assert.Equal("Far", results[2].Chunk.MethodName);
        // Scores are descending and the exact match is ~1.0.
        Assert.True(results[0].Score >= results[1].Score);
        Assert.True(results[1].Score >= results[2].Score);
        Assert.True(results[0].Score > 0.99f);
    }

    [Fact]
    public async Task SearchAsync_honours_the_limit()
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default,
        [
            Chunk("a.cs", 1, [1f, 0f]),
            Chunk("b.cs", 1, [0f, 1f]),
            Chunk("c.cs", 1, [1f, 1f]),
        ]);

        var results = await store.SearchAsync(CollectionNames.Default, new[] { 1f, 0f }, limit: 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetByFileAsync_returns_matching_chunks_ordered_by_start_line()
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default,
        [
            Chunk("target.cs", 30, [1f, 0f], "Third"),
            Chunk("target.cs", 10, [1f, 0f], "First"),
            Chunk("other.cs", 1, [1f, 0f], "Elsewhere"),
            Chunk("target.cs", 20, [1f, 0f], "Second"),
        ]);

        var chunks = await store.GetByFileAsync(CollectionNames.Default, "target.cs");

        Assert.Equal(["First", "Second", "Third"], chunks.Select(c => c.MethodName));
    }

    private static CodeChunk SymbolChunk(string relativePath, string className, string methodName, int startLine = 1) => new()
    {
        RelativePath = relativePath,
        ClassName = className,
        MethodName = methodName,
        StartLine = startLine,
        EndLine = startLine + 1,
        Content = $"{className}.{methodName}",
        Embedding = new[] { 1f, 0f },
    };

    [Fact]
    public async Task GetBySymbolAsync_matches_by_class_only()
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default,
        [
            SymbolChunk("user.cs", "UserRepository", "GetById", startLine: 10),
            SymbolChunk("user.cs", "UserRepository", "Save", startLine: 20),
            SymbolChunk("order.cs", "OrderRepository", "GetById", startLine: 1),
        ]);

        var chunks = await store.GetBySymbolAsync(CollectionNames.Default, "UserRepository", methodName: null);

        Assert.Equal(["GetById", "Save"], chunks.Select(c => c.MethodName));
        Assert.All(chunks, c => Assert.Equal("UserRepository", c.ClassName));
    }

    [Fact]
    public async Task GetBySymbolAsync_matches_by_method_only()
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default,
        [
            SymbolChunk("user.cs", "UserRepository", "GetById", startLine: 10),
            SymbolChunk("order.cs", "OrderRepository", "GetById", startLine: 1),
            SymbolChunk("user.cs", "UserRepository", "Save", startLine: 20),
        ]);

        var chunks = await store.GetBySymbolAsync(CollectionNames.Default, className: null, methodName: "GetById");

        // Ordered by relative path then start line: order.cs before user.cs.
        Assert.Equal(["OrderRepository", "UserRepository"], chunks.Select(c => c.ClassName));
        Assert.All(chunks, c => Assert.Equal("GetById", c.MethodName));
    }

    [Fact]
    public async Task GetBySymbolAsync_matches_by_both_class_and_method_case_insensitively()
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default,
        [
            SymbolChunk("user.cs", "UserRepository", "GetById", startLine: 10),
            SymbolChunk("user.cs", "UserRepository", "Save", startLine: 20),
            SymbolChunk("order.cs", "OrderRepository", "GetById", startLine: 1),
        ]);

        // Different casing on both filters still matches the exact name (case-insensitive contract).
        var chunks = await store.GetBySymbolAsync(CollectionNames.Default, "userrepository", "GETBYID");

        var only = Assert.Single(chunks);
        Assert.Equal("UserRepository", only.ClassName);
        Assert.Equal("GetById", only.MethodName);
    }

    [Fact]
    public async Task GetBySymbolAsync_returns_empty_when_no_match()
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default, [SymbolChunk("user.cs", "UserRepository", "GetById")]);

        Assert.Empty(await store.GetBySymbolAsync(CollectionNames.Default, "MissingClass", methodName: null));
    }

    [Fact]
    public async Task GetByFileAsync_returns_empty_when_no_match()
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default, [Chunk("a.cs", 1, [1f, 0f])]);

        Assert.Empty(await store.GetByFileAsync(CollectionNames.Default, "missing.cs"));
    }

    [Fact]
    public async Task UpsertAsync_replaces_a_chunk_with_the_same_identity()
    {
        var store = new LocalCodeChunkStore();
        // Same RelativePath + line span => same ChunkId => the second upsert replaces the first.
        await store.UpsertAsync(CollectionNames.Default, [Chunk("a.cs", 5, [1f, 0f], "Old")]);
        await store.UpsertAsync(CollectionNames.Default, [Chunk("a.cs", 5, [1f, 0f], "New")]);

        var chunks = await store.GetByFileAsync(CollectionNames.Default, "a.cs");

        var only = Assert.Single(chunks);
        Assert.Equal("New", only.MethodName);
    }

    [Fact]
    public async Task Collections_are_isolated_from_each_other_for_search_and_get_by_file()
    {
        // Proves SYNTH-17 isolation: chunks upserted under one collection must never surface
        // from a search or get-by-file scoped to another. Both chunks share the same file name
        // and embedding on purpose, so only the collection boundary can keep them apart.
        var store = new LocalCodeChunkStore();
        var chunkA = Chunk("shared.cs", 1, [1f, 0f], "FromRepoA");
        var chunkB = Chunk("shared.cs", 1, [1f, 0f], "FromRepoB");

        await store.UpsertAsync("repo-a", [chunkA]);
        await store.UpsertAsync("repo-b", [chunkB]);

        // Search stays scoped: each collection returns only its own chunk.
        var hitsA = await store.SearchAsync("repo-a", new[] { 1f, 0f }, limit: 10);
        var hitsB = await store.SearchAsync("repo-b", new[] { 1f, 0f }, limit: 10);
        Assert.Equal("FromRepoA", Assert.Single(hitsA).Chunk.MethodName);
        Assert.Equal("FromRepoB", Assert.Single(hitsB).Chunk.MethodName);

        // Get-by-file stays scoped too, even though the relative path is identical.
        var fileA = await store.GetByFileAsync("repo-a", "shared.cs");
        var fileB = await store.GetByFileAsync("repo-b", "shared.cs");
        Assert.Equal("FromRepoA", Assert.Single(fileA).MethodName);
        Assert.Equal("FromRepoB", Assert.Single(fileB).MethodName);

        // A collection that was never written to is empty, not a view over everything.
        Assert.Empty(await store.SearchAsync("repo-c", new[] { 1f, 0f }, limit: 10));
        Assert.Empty(await store.GetByFileAsync("repo-c", "shared.cs"));
    }

    [Fact]
    public async Task DeleteCollectionAsync_drops_the_collection_leaving_others_intact()
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync("repo-a", [Chunk("a.cs", 1, [1f, 0f], "FromRepoA")]);
        await store.UpsertAsync("repo-b", [Chunk("b.cs", 1, [1f, 0f], "FromRepoB")]);

        await store.DeleteCollectionAsync("repo-a");

        // The deleted collection reads as empty; the other collection is untouched.
        Assert.Empty(await store.GetByFileAsync("repo-a", "a.cs"));
        Assert.Empty(await store.SearchAsync("repo-a", new[] { 1f, 0f }, limit: 10));
        Assert.Equal("FromRepoB", Assert.Single(await store.GetByFileAsync("repo-b", "b.cs")).MethodName);
    }

    [Fact]
    public async Task DeleteCollectionAsync_is_a_noop_for_an_unknown_collection()
    {
        var store = new LocalCodeChunkStore();

        // Should not throw for a collection that was never created.
        await store.DeleteCollectionAsync("never-created");
    }
}
