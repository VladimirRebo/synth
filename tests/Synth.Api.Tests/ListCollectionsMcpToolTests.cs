using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Synth.Api.Mcp;
using Synth.Infrastructure.Vcs;
using Synth.Domain.Vcs;

namespace Synth.Api.Tests;

// Proves SYNTH-43 (part 1): the `list_collections` MCP tool returns the indexed collections straight
// from IRepositoryRegistry — the same source GET /repositories reads — with no duplicated logic.
// Runs offline against a seeded in-memory registry (no Mongo/Qdrant needed).
public class ListCollectionsMcpToolTests
{
    private static async Task<InMemoryRepositoryRegistry> SeededRegistry(params string[] collections)
    {
        var registry = new InMemoryRepositoryRegistry();
        foreach (var collection in collections)
        {
            await registry.UpsertAsync(new RepositoryEntry
            {
                Collection = collection,
                SourceType = "local",
                Source = $"/tmp/{collection}",
                LastIndexedAt = DateTime.UtcNow,
                ChunkCount = 1,
            });
        }

        return registry;
    }

    [Fact]
    public async Task List_collections_returns_every_registered_entry()
    {
        var registry = await SeededRegistry("repo-a", "repo-b", "repo-c");

        var listed = await ListCollectionsTool.ListCollectionsAsync(registry);

        Assert.Equal(3, listed.Count);
        Assert.Contains(listed, e => e.Collection == "repo-a");
        Assert.Contains(listed, e => e.Collection == "repo-b");
        Assert.Contains(listed, e => e.Collection == "repo-c");
    }

    [Fact]
    public async Task List_collections_returns_empty_when_nothing_is_indexed()
    {
        var listed = await ListCollectionsTool.ListCollectionsAsync(new InMemoryRepositoryRegistry());

        Assert.Empty(listed);
    }

    [Fact]
    public void List_collections_tool_is_registered_on_the_mcp_server()
    {
        using var factory = new TestApiFactory();

        var tools = factory.Services.GetServices<McpServerTool>();

        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "list_collections");
    }
}
