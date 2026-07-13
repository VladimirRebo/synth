using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synth.Api.Graph;
using Synth.Domain.Graph;
using Synth.Infrastructure.Graph;
using Synth.Domain;

namespace Synth.Api.Tests;

// Drives GET /callers and GET /callees over HTTP, proving they wrap the same ICodeGraphStore the
// MCP tools do and are scoped by collection. Hermetic: the DI ICodeGraphStore is swapped for an
// in-memory store seeded with known edges (no Mongo/Docker), mirroring SearchEndpointTests'
// fake-dependency approach.
public class CallGraphControllerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly WebApplicationFactory<Program> _factory;

    public CallGraphControllerTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static CallEdge Edge(string collection, string caller, string callee) =>
        new(collection, caller, callee, $"{caller}.cs", 42);

    private HttpClient CreateClient(ICodeGraphStore store) =>
        _factory
            .WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<ICodeGraphStore>();
                    services.AddSingleton(store);
                }))
            .CreateClient();

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

    private static async Task<List<CallEdge>> ReadEdges(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<CallEdge>>(raw, JsonOptions) ?? [];
    }

    [Fact]
    public async Task Callers_returns_the_edges_into_a_known_symbol()
    {
        var client = CreateClient(await SeededStore());

        var response = await client.GetAsync("/callers?symbol=App.Repo.Load");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var edges = await ReadEdges(response);
        Assert.Equal(2, edges.Count);
        Assert.All(edges, e => Assert.Equal("App.Repo.Load", e.Callee));
        Assert.Contains(edges, e => e.Caller == "App.Service.Run");
        Assert.Contains(edges, e => e.Caller == "App.Worker.Tick");
    }

    [Fact]
    public async Task Callees_returns_the_edges_out_of_a_known_symbol()
    {
        var client = CreateClient(await SeededStore());

        var response = await client.GetAsync("/callees?symbol=App.Repo.Load");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var edge = Assert.Single(await ReadEdges(response));
        Assert.Equal("App.Db.Query", edge.Callee);
    }

    [Fact]
    public async Task Unknown_symbol_returns_an_empty_list_not_an_error()
    {
        var client = CreateClient(await SeededStore());

        var response = await client.GetAsync("/callers?symbol=App.Nope.Missing");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ReadEdges(response));
    }

    [Theory]
    [InlineData("/callers")]
    [InlineData("/callees")]
    public async Task Missing_symbol_returns_400(string path)
    {
        var client = CreateClient(new InMemoryCodeGraphStore());

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Blank_symbol_returns_400()
    {
        var client = CreateClient(new InMemoryCodeGraphStore());

        var response = await client.GetAsync("/callers?symbol=%20%20");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Query_is_scoped_to_the_requested_collection()
    {
        var store = new InMemoryCodeGraphStore();
        await store.ReplaceEdgesAsync("repo-a", [Edge("repo-a", "A.Source", "A.Target")]);
        await store.ReplaceEdgesAsync("repo-b", [Edge("repo-b", "A.Source", "B.Target")]);
        var client = CreateClient(store);

        var inA = await ReadEdges(await client.GetAsync("/callees?symbol=A.Source&collection=repo-a"));
        var inB = await ReadEdges(await client.GetAsync("/callees?symbol=A.Source&collection=repo-b"));
        var inDefault = await ReadEdges(await client.GetAsync("/callees?symbol=A.Source"));

        Assert.Equal("A.Target", Assert.Single(inA).Callee);
        Assert.Equal("B.Target", Assert.Single(inB).Callee);
        // The default collection has no edges — proving the query really is collection-scoped.
        Assert.Empty(inDefault);
    }
}
