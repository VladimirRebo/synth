using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Synth.Api.Mcp;
using Synth.Core;

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

    [Fact]
    public async Task Get_symbol_tool_returns_matching_chunks_projected_to_results()
    {
        var store = new LocalCodeChunkStore();
        await store.UpsertAsync(CollectionNames.Default,
        [
            Chunk("repo.cs", "UserRepository", "GetUserById", startLine: 10),
            Chunk("hash.cs", "Hasher", "ComputeChecksum", startLine: 20),
        ]);

        // Exact class lookup, case-insensitive — no vector search involved.
        var results = await GetSymbolTool.GetSymbolAsync(store, className: "userrepository", methodName: null);

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

        var results = await GetSymbolTool.GetSymbolAsync(store, className: null, methodName: "GetById");

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("GetById", r.MethodName));
    }

    [Fact]
    public async Task Get_symbol_tool_rejects_when_neither_class_nor_method_is_given()
    {
        var store = new LocalCodeChunkStore();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            GetSymbolTool.GetSymbolAsync(store, className: null, methodName: "   "));

        Assert.Contains("at least one", ex.Message);
    }

    [Fact]
    public void Get_symbol_tool_is_registered_on_the_mcp_server()
    {
        using var factory = new WebApplicationFactory<Program>();

        var tools = factory.Services.GetServices<McpServerTool>();

        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "get_symbol");
    }
}
