using Synth.Core;

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

        await store.UpsertAsync([far, near, mid]);

        var results = await store.SearchAsync(new[] { 1f, 0f, 0f }, limit: 3);

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
        await store.UpsertAsync(
        [
            Chunk("a.cs", 1, [1f, 0f]),
            Chunk("b.cs", 1, [0f, 1f]),
            Chunk("c.cs", 1, [1f, 1f]),
        ]);

        var results = await store.SearchAsync(new[] { 1f, 0f }, limit: 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetByFileAsync_returns_matching_chunks_ordered_by_start_line()
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(
        [
            Chunk("target.cs", 30, [1f, 0f], "Third"),
            Chunk("target.cs", 10, [1f, 0f], "First"),
            Chunk("other.cs", 1, [1f, 0f], "Elsewhere"),
            Chunk("target.cs", 20, [1f, 0f], "Second"),
        ]);

        var chunks = await store.GetByFileAsync("target.cs");

        Assert.Equal(["First", "Second", "Third"], chunks.Select(c => c.MethodName));
    }

    [Fact]
    public async Task GetByFileAsync_returns_empty_when_no_match()
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync([Chunk("a.cs", 1, [1f, 0f])]);

        Assert.Empty(await store.GetByFileAsync("missing.cs"));
    }

    [Fact]
    public async Task UpsertAsync_replaces_a_chunk_with_the_same_identity()
    {
        var store = new LocalCodeChunkStore();
        // Same RelativePath + line span => same ChunkId => the second upsert replaces the first.
        await store.UpsertAsync([Chunk("a.cs", 5, [1f, 0f], "Old")]);
        await store.UpsertAsync([Chunk("a.cs", 5, [1f, 0f], "New")]);

        var chunks = await store.GetByFileAsync("a.cs");

        var only = Assert.Single(chunks);
        Assert.Equal("New", only.MethodName);
    }
}
