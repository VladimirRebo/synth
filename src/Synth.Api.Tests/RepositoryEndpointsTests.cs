using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synth.Api.Vcs;

namespace Synth.Api.Tests;

// Drives DELETE /repositories/{collection} over HTTP. Hermetic: the registry is swapped for a
// seeded in-memory instance (chunk/graph stores fall back to their in-memory defaults with no
// Qdrant/Mongo configured), mirroring CallGraphEndpointTests' fake-dependency approach. Proves the
// deleted collection no longer lists, and that deleting an unknown collection is a 404.
public class RepositoryEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RepositoryEndpointsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient CreateClient(IRepositoryRegistry registry) =>
        _factory
            .WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IRepositoryRegistry>();
                    services.AddSingleton(registry);
                }))
            .CreateClient();

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
    public async Task Delete_removes_the_collection_from_a_subsequent_list()
    {
        var registry = await SeededRegistry("repo-a", "repo-b");
        var client = CreateClient(registry);

        var deleteResponse = await client.DeleteAsync("/repositories/repo-a");

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var listed = await client.GetFromJsonAsync<List<RepositoryEntry>>("/repositories");
        Assert.NotNull(listed);
        Assert.DoesNotContain(listed!, e => e.Collection == "repo-a");
        Assert.Contains(listed!, e => e.Collection == "repo-b");
    }

    [Fact]
    public async Task Delete_of_an_unknown_collection_returns_404()
    {
        var client = CreateClient(await SeededRegistry("repo-a"));

        var response = await client.DeleteAsync("/repositories/never-indexed");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
