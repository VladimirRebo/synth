using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Synth.Api.Mcp;
using Synth.Core;

namespace Synth.Api.Tests;

// Proves SYNTH-12: the `search_code` MCP tool wraps SYNTH-11's CodeSearchService and is wired
// into the API's MCP server. Runs fully offline — a deterministic fake embedding generator plus
// the in-memory LocalCodeChunkStore, mirroring the SYNTH-11 tests (no live Ollama/Qdrant/Docker).
public class CodeSearchMcpToolTests
{
    // Returns the same vector for every text, so all seeded chunks and the query share an equal
    // cosine score and ranking is decided purely by CodeSearchService's rerank (chunk type +
    // camelCase keyword boost) — the same setup SYNTH-11's tests rely on.
    private sealed class ConstantEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private static readonly float[] Vector = [1f, 0f, 0f, 0f];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(Vector))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static readonly float[] SharedVector = [1f, 0f, 0f, 0f];

    private static CodeChunk Chunk(string relativePath, string className, string methodName, int startLine = 1) =>
        new()
        {
            RelativePath = relativePath,
            ClassName = className,
            MethodName = methodName,
            ChunkType = ChunkType.Method,
            Content = $"public void {methodName}() {{ /* {className} */ }}",
            StartLine = startLine,
            EndLine = startLine + 1,
            Embedding = SharedVector,
        };

    private static CodeSearchService ServiceOver(ICodeChunkStore store) =>
        new(new ConstantEmbeddingGenerator(), store, new QueryExpander());

    [Fact]
    public async Task Search_code_tool_returns_matching_chunks_projected_to_results()
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default, [
            Chunk("repo.cs", "UserRepository", "GetUserById", startLine: 10),
            Chunk("hash.cs", "Hasher", "ComputeChecksum", startLine: 20),
        ]);

        // "user" matches GetUserById via camelCase tokenization -> ranks first (keyword boost).
        var results = await CodeSearchTool.SearchCodeAsync(ServiceOver(store), "user", limit: 5);

        Assert.NotEmpty(results);
        var top = results[0];
        Assert.Equal("GetUserById", top.MethodName);
        Assert.Equal("UserRepository", top.ClassName);
        Assert.Equal("repo.cs", top.RelativePath);
        Assert.Contains("UserRepository", top.QualifiedName);
        Assert.Contains("GetUserById", top.Snippet);
    }

    [Fact]
    public async Task Search_code_tool_honours_the_limit()
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default, Enumerable.Range(0, 6)
            .Select(i => Chunk($"f{i}.cs", $"Class{i}", $"Method{i}", startLine: i + 1)));

        var results = await CodeSearchTool.SearchCodeAsync(ServiceOver(store), "anything", limit: 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Search_code_tool_returns_empty_for_blank_query()
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default, [Chunk("f.cs", "A", "M")]);

        var results = await CodeSearchTool.SearchCodeAsync(ServiceOver(store), "   ", limit: 5);

        Assert.Empty(results);
    }

    [Fact]
    public void Search_code_tool_is_registered_on_the_mcp_server()
    {
        // The tool being resolvable as an McpServerTool proves Program.cs wired it into
        // AddMcpServer().WithTools<CodeSearchTool>() over the HTTP transport.
        using var factory = new WebApplicationFactory<Program>();

        var tools = factory.Services.GetServices<McpServerTool>();

        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "search_code");
    }
}
