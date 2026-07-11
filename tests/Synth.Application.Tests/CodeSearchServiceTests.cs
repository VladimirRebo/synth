using Microsoft.Extensions.AI;
using Synth.Application;
using Synth.Core;
using Synth.Domain;

namespace Synth.Application.Tests;

// Proves SYNTH-11: CodeSearchService over-fetches from the store, reranks via
// chunk-type weight × camelCase keyword boost, and enforces the dedup limits — all
// with a deterministic fake embedding generator + the in-memory LocalCodeChunkStore
// (no live Ollama/Qdrant/Docker).
public class CodeSearchServiceTests
{
    // Embedding generator that returns a caller-configured vector for every text, so tests
    // fully control the raw vector score the store produces. All seeded chunks and the query
    // share the same vector => equal cosine scores => ranking is decided purely by reranking.
    private sealed class ConstantEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly float[] _vector;

        public ConstantEmbeddingGenerator(float[] vector) => _vector = vector;

        public string? LastValue { get; private set; }

        // How many times GenerateAsync was invoked — used to prove all-collections search embeds
        // the query exactly once and reuses the vector across every collection's store search.
        public int Calls { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var list = values.ToList();
            LastValue = list.LastOrDefault();
            var embeddings = new GeneratedEmbeddings<Embedding<float>>(
                list.Select(_ => new Embedding<float>(_vector)));
            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static readonly float[] SharedVector = [1f, 0f, 0f, 0f];

    private static CodeChunk Chunk(
        string relativePath,
        string className,
        string methodName,
        ChunkType chunkType,
        int startLine = 1) => new CodeChunk
        {
            RelativePath = relativePath,
            ClassName = className,
            MethodName = methodName,
            ChunkType = chunkType,
            Content = $"{className}.{methodName}",
            StartLine = startLine,
            EndLine = startLine + 1,
            Embedding = SharedVector,
        };

    private static CodeSearchService ServiceFor(ICodeChunkStore store, float[]? queryVector = null) =>
        new(new ConstantEmbeddingGenerator(queryVector ?? SharedVector), store, new QueryExpander());

    [Fact]
    public async Task SearchAsync_chunk_type_weight_reorders_equal_vector_scores()
    {
        // Two chunks with identical vector score (same embedding as the query) but different
        // chunk types. Names deliberately share no tokens with the query, so only the
        // chunk-type weight (Class 1.15 > MethodBody 0.90) can decide the order.
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default, [
            Chunk("a.cs", "Alpha", "", ChunkType.MethodBody, startLine: 10),
            Chunk("b.cs", "Beta", "", ChunkType.Class, startLine: 20),
        ]);

        var results = await ServiceFor(store).SearchAsync(CollectionNames.Default, "zzz unrelated", limit: 5);

        Assert.Equal(2, results.Count);
        Assert.Equal("Beta", results[0].Chunk.ClassName);   // Class weight wins
        Assert.Equal("Alpha", results[1].Chunk.ClassName);
        Assert.True(results[0].Score > results[1].Score);   // The score reflects the reordering
    }

    [Fact]
    public async Task SearchAsync_camelCase_keyword_boost_ranks_name_match_first()
    {
        // Same vector score and same chunk type for both; only the keyword boost differs.
        // The query token "user" matches GetUserById via camelCase tokenization but not
        // ComputeChecksum, so the matching method must rank first.
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default, [
            Chunk("svc.cs", "Repo", "ComputeChecksum", ChunkType.Method, startLine: 5),
            Chunk("svc.cs", "Repo", "GetUserById", ChunkType.Method, startLine: 40),
        ]);

        var results = await ServiceFor(store).SearchAsync(CollectionNames.Default, "user", limit: 5);

        Assert.Equal("GetUserById", results[0].Chunk.MethodName);
        Assert.Equal("ComputeChecksum", results[1].Chunk.MethodName);
    }

    [Fact]
    public async Task SearchAsync_limits_two_hits_per_method_name()
    {
        // Three distinct chunks (different paths/classes) that all share the method name
        // "Handle": dedup must keep at most two of them.
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default, [
            Chunk("one.cs", "A", "Handle", ChunkType.Method),
            Chunk("two.cs", "B", "Handle", ChunkType.Method),
            Chunk("three.cs", "C", "Handle", ChunkType.Method),
        ]);

        var results = await ServiceFor(store).SearchAsync(CollectionNames.Default, "handle", limit: 10);

        Assert.Equal(2, results.Count);
        Assert.All(results, scored => Assert.Equal("Handle", scored.Chunk.MethodName));
    }

    [Fact]
    public async Task SearchAsync_dedups_identical_path_class_method_to_one()
    {
        // Two chunks with the same RelativePath::ClassName.MethodName (e.g. a method's head
        // and body) must collapse to a single hit.
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default, [
            Chunk("f.cs", "Svc", "Run", ChunkType.MethodHead, startLine: 1),
            Chunk("f.cs", "Svc", "Run", ChunkType.MethodBody, startLine: 3),
        ]);

        var results = await ServiceFor(store).SearchAsync(CollectionNames.Default, "run", limit: 10);

        Assert.Single(results);
    }

    [Fact]
    public async Task SearchAsync_truncates_to_limit()
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default, Enumerable.Range(0, 6)
            .Select(i => Chunk($"f{i}.cs", $"Class{i}", $"Method{i}", ChunkType.Class, startLine: i + 1)));

        var results = await ServiceFor(store).SearchAsync(CollectionNames.Default, "anything", limit: 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchAsync_expands_cyrillic_query_before_embedding()
    {
        var store = new LocalCodeChunkStore();
        var generator = new ConstantEmbeddingGenerator(SharedVector);
        var service = new CodeSearchService(generator, store, new QueryExpander());

        await service.SearchAsync(CollectionNames.Default, "найти пользователь", limit: 3);

        // The text actually embedded carries the appended English terms.
        Assert.Contains("find", generator.LastValue);
        Assert.Contains("user", generator.LastValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SearchAsync_returns_empty_for_non_positive_limit(int limit)
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default, [Chunk("f.cs", "A", "M", ChunkType.Method)]);

        var results = await ServiceFor(store).SearchAsync(CollectionNames.Default, "m", limit);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_leaves_collection_unset_on_the_single_collection_path()
    {
        // Single-collection search doesn't populate Collection — the caller already knows it, and the
        // client hides the per-result collection label in that mode.
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default, [Chunk("f.cs", "A", "M", ChunkType.Method)]);

        var results = await ServiceFor(store).SearchAsync(CollectionNames.Default, "m", limit: 5);

        Assert.All(results, scored => Assert.Null(scored.Collection));
    }

    [Fact]
    public async Task SearchAllCollectionsAsync_merges_results_from_every_collection_with_one_embedding_call()
    {
        // Two populated collections, each with a chunk sharing the query token "handle": the search
        // must fan out over both, merge into one ranked list, tag each result with its collection,
        // and embed the query exactly once (reusing the vector across both store searches).
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync("repo-a", [Chunk("a.cs", "Alpha", "Handle", ChunkType.Method, startLine: 10)]);
        await store.UpsertAsync("repo-b", [Chunk("b.cs", "Beta", "Handle", ChunkType.Method, startLine: 20)]);
        var generator = new ConstantEmbeddingGenerator(SharedVector);
        var service = new CodeSearchService(generator, store, new QueryExpander());

        var results = await service.SearchAllCollectionsAsync(["repo-a", "repo-b"], "handle", limit: 10);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, scored => scored.Collection == "repo-a" && scored.Chunk.ClassName == "Alpha");
        Assert.Contains(results, scored => scored.Collection == "repo-b" && scored.Chunk.ClassName == "Beta");
        Assert.Equal(1, generator.Calls); // embedded once, reused across every collection's store search
    }

    [Fact]
    public async Task SearchAllCollectionsAsync_returns_empty_for_no_collections()
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default, [Chunk("f.cs", "A", "M", ChunkType.Method)]);

        var results = await ServiceFor(store).SearchAllCollectionsAsync([], "m", limit: 5);

        Assert.Empty(results);
    }
}
