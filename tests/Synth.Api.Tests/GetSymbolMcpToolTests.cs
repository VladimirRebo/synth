using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Synth.Api.Mcp;
using Synth.Domain;
using Synth.Domain.Vcs;
using Synth.Infrastructure.Storage;
using Synth.Infrastructure.Vcs;

namespace Synth.Api.Tests;

// Proves SYNTH-42 (part 1): the `get_symbol` MCP tool does an exact class/method lookup over the
// vector store (no embedding call), projecting matches to SymbolResult, and rejects a call with
// neither name given. Runs fully offline over the in-memory LocalCodeChunkStore.
public class GetSymbolMcpToolTests
{
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
        };

    // A registry reporting CollectionNames.Default so an omitted `collection` argument resolves back
    // to it (CollectionNameResolver's fast path), matching this file's pre-existing intent of "no
    // collection specified" meaning the default collection.
    private static async Task<IRepositoryRegistry> DefaultRegistry()
    {
        var registry = new InMemoryRepositoryRegistry();
        await registry.UpsertAsync(new RepositoryEntry
        {
            Collection = CollectionNames.Default,
            SourceType = "local",
            Source = "/tmp/default",
            LastIndexedAt = DateTime.UtcNow,
            ChunkCount = 0,
        });
        return registry;
    }

    [Fact]
    public async Task Get_symbol_tool_returns_matching_chunks_projected_to_results()
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default,
        [
            Chunk("repo.cs", "UserRepository", "GetUserById", startLine: 10),
            Chunk("hash.cs", "Hasher", "ComputeChecksum", startLine: 20),
        ]);
        var registry = await DefaultRegistry();

        // Exact class lookup, case-insensitive — no vector search involved.
        var results = await GetSymbolTool.GetSymbolAsync(store, registry, className: "userrepository", methodName: null);

        var only = Assert.Single(results);
        Assert.Equal("UserRepository", only.ClassName);
        Assert.Equal("GetUserById", only.MethodName);
        Assert.Equal("repo.cs", only.RelativePath);
        Assert.Contains("GetUserById", only.Snippet);
    }

    [Fact]
    public async Task Get_symbol_tool_matches_by_method_name()
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default,
        [
            Chunk("repo.cs", "UserRepository", "GetById", startLine: 10),
            Chunk("order.cs", "OrderRepository", "GetById", startLine: 5),
        ]);
        var registry = await DefaultRegistry();

        var results = await GetSymbolTool.GetSymbolAsync(store, registry, className: null, methodName: "GetById");

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("GetById", r.MethodName));
    }

    [Fact]
    public async Task Get_symbol_tool_rejects_when_neither_class_nor_method_is_given()
    {
        var store = new LocalCodeChunkStore();
        var registry = new InMemoryRepositoryRegistry();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            GetSymbolTool.GetSymbolAsync(store, registry, className: null, methodName: "   "));

        Assert.Contains("at least one", ex.Message);
    }

    [Fact]
    public void Get_symbol_tool_is_registered_on_the_mcp_server()
    {
        using var factory = new TestApiFactory();

        var tools = factory.Services.GetServices<McpServerTool>();

        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "get_symbol");
    }
}
