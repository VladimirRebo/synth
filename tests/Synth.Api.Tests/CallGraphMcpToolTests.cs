using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Synth.Api.Graph;
using Synth.Domain.Graph;
using Synth.Infrastructure.Graph;
using Synth.Domain;

namespace Synth.Api.Tests;

// Proves SYNTH-27: the find_callers/find_callees MCP tools wrap SYNTH-25's ICodeGraphStore and are
// wired into the API's MCP server. Runs fully offline against the in-memory store (the same
// production fallback used when no Mongo is configured), mirroring CodeSearchMcpToolTests.
public class CallGraphMcpToolTests
{
    private static CallEdge Edge(string collection, string caller, string callee) =>
        new(collection, caller, callee, $"{caller}.cs", 1);

    private static async Task<InMemoryCodeGraphStore> SeededStore()
    {
        var store = new InMemoryCodeGraphStore();
        await store.ReplaceEdgesAsync(CollectionNames.Default, [
            Edge(CollectionNames.Default, "App.Service.Run", "App.Repo.Load"),
            Edge(CollectionNames.Default, "App.Worker.Tick", "App.Repo.Load"),
            Edge(CollectionNames.Default, "App.Repo.Load", "App.Db.Query"),
        ]);
        return store;
    }

    [Fact]
    public async Task Find_callers_returns_edges_into_the_symbol()
    {
        var store = await SeededStore();

        var callers = await CallGraphTool.FindCallersAsync(store, "App.Repo.Load");

        Assert.Equal(2, callers.Count);
        Assert.All(callers, e => Assert.Equal("App.Repo.Load", e.Callee));
        Assert.Contains(callers, e => e.Caller == "App.Service.Run");
        Assert.Contains(callers, e => e.Caller == "App.Worker.Tick");
    }

    [Fact]
    public async Task Find_callees_returns_edges_out_of_the_symbol()
    {
        var store = await SeededStore();

        var callees = await CallGraphTool.FindCalleesAsync(store, "App.Repo.Load");

        var edge = Assert.Single(callees);
        Assert.Equal("App.Repo.Load", edge.Caller);
        Assert.Equal("App.Db.Query", edge.Callee);
    }

    [Fact]
    public async Task Find_callers_returns_empty_for_unknown_symbol()
    {
        var store = await SeededStore();

        var callers = await CallGraphTool.FindCallersAsync(store, "App.Nope.Missing");

        Assert.Empty(callers);
    }

    [Fact]
    public async Task Find_callees_is_scoped_to_the_requested_collection()
    {
        var store = new InMemoryCodeGraphStore();
        await store.ReplaceEdgesAsync("repo-a", [Edge("repo-a", "A.Source", "A.Target")]);
        await store.ReplaceEdgesAsync("repo-b", [Edge("repo-b", "A.Source", "B.Target")]);

        var inA = await CallGraphTool.FindCalleesAsync(store, "A.Source", "repo-a");
        var inB = await CallGraphTool.FindCalleesAsync(store, "A.Source", "repo-b");

        Assert.Equal("A.Target", Assert.Single(inA).Callee);
        Assert.Equal("B.Target", Assert.Single(inB).Callee);
    }

    [Theory]
    [InlineData("find_callers")]
    [InlineData("find_callees")]
    public void Call_graph_tools_are_registered_on_the_mcp_server(string toolName)
    {
        // Being resolvable as McpServerTools proves Program.cs wired them into
        // AddMcpServer().WithTools<CallGraphTool>() over the HTTP transport.
        using var factory = new TestApiFactory();

        var tools = factory.Services.GetServices<McpServerTool>();

        Assert.Contains(tools, tool => tool.ProtocolTool.Name == toolName);
    }
}
