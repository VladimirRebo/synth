using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synth.Api.Vcs;
using Synth.Domain.Vcs;

namespace Synth.Api.Tests;

// Drives DELETE /repositories/{collection} over HTTP. Hermetic: the registry is swapped for a
// seeded in-memory instance (chunk/graph stores fall back to their in-memory defaults with no
// Qdrant/Mongo configured), mirroring CallGraphEndpointTests' fake-dependency approach. Proves the
// deleted collection no longer lists, and that deleting an unknown collection is a 404.
public class RepositoryEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RepositoryEndpointsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient CreateClient(IRepositoryRegistry registry, string? workspaceRoot = null) =>
        _factory
            .WithWebHostBuilder(builder =>
            {
                // Point GitRepoService at a temp workspace so DELETE's checkout cleanup never touches
                // the real ~/.synth/workspaces.
                if (workspaceRoot is not null)
                    builder.UseSetting("Vcs:WorkspaceRoot", workspaceRoot);

                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IRepositoryRegistry>();
                    services.AddSingleton(registry);
                });
            })
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

    // Seeds three repos with distinct LastIndexedAt so the endpoint's most-recently-indexed-first
    // order is deterministic: newest -> repo-2, repo-1, repo-0.
    private static async Task<InMemoryRepositoryRegistry> SeededRegistryOrdered()
    {
        var registry = new InMemoryRepositoryRegistry();
        for (var i = 0; i < 3; i++)
        {
            await registry.UpsertAsync(new RepositoryEntry
            {
                Collection = $"repo-{i}",
                SourceType = "local",
                Source = $"/tmp/repo-{i}",
                LastIndexedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                ChunkCount = 1,
            });
        }

        return registry;
    }

    [Fact]
    public async Task List_without_pagination_returns_every_repository()
    {
        var client = CreateClient(await SeededRegistry("repo-a", "repo-b", "repo-c"));

        var listed = await client.GetFromJsonAsync<List<RepositoryEntry>>("/repositories");

        Assert.NotNull(listed);
        Assert.Equal(3, listed!.Count);
    }

    [Fact]
    public async Task List_with_limit_and_offset_returns_the_requested_slice()
    {
        var client = CreateClient(await SeededRegistryOrdered());

        // Ordered newest-first (repo-2, repo-1, repo-0); skip 1, take 1 -> repo-1.
        var listed = await client.GetFromJsonAsync<List<RepositoryEntry>>("/repositories?limit=1&offset=1");

        Assert.NotNull(listed);
        Assert.Equal(new[] { "repo-1" }, listed!.Select(e => e.Collection));
    }

    [Fact]
    public async Task List_with_a_negative_limit_returns_400()
    {
        var client = CreateClient(await SeededRegistry("repo-a"));

        var response = await client.GetAsync("/repositories?limit=-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

    [Theory]
    [InlineData("github")]
    [InlineData("gitlab")]
    public async Task Delete_of_a_cloned_remote_removes_its_on_disk_checkout(string sourceType)
    {
        var workspaceRoot = Directory.CreateTempSubdirectory("synth-gc-workspace-");
        try
        {
            // The checkout lives at {WorkspaceRoot}/{collection} (slug == collection name).
            var checkout = Directory.CreateDirectory(Path.Combine(workspaceRoot.FullName, "remote-repo"));
            File.WriteAllText(Path.Combine(checkout.FullName, "README.md"), "cloned");

            var registry = new InMemoryRepositoryRegistry();
            await registry.UpsertAsync(new RepositoryEntry
            {
                Collection = "remote-repo",
                SourceType = sourceType,
                Source = "https://example.com/acme/remote-repo.git",
                LastIndexedAt = DateTime.UtcNow,
                ChunkCount = 1,
            });
            var client = CreateClient(registry, workspaceRoot.FullName);

            var response = await client.DeleteAsync("/repositories/remote-repo");

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.False(Directory.Exists(checkout.FullName));
        }
        finally
        {
            workspaceRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Delete_of_a_local_collection_leaves_any_workspace_directory_untouched()
    {
        var workspaceRoot = Directory.CreateTempSubdirectory("synth-gc-workspace-");
        try
        {
            // A local source is never cloned, so DELETE must attempt no checkout removal. A stray
            // same-named directory under the workspace root proves the local branch is skipped: it
            // must survive the delete.
            var strayDirectory = Directory.CreateDirectory(Path.Combine(workspaceRoot.FullName, "local-repo"));

            var registry = new InMemoryRepositoryRegistry();
            await registry.UpsertAsync(new RepositoryEntry
            {
                Collection = "local-repo",
                SourceType = "local",
                Source = "/some/local/path",
                LastIndexedAt = DateTime.UtcNow,
                ChunkCount = 1,
            });
            var client = CreateClient(registry, workspaceRoot.FullName);

            var response = await client.DeleteAsync("/repositories/local-repo");

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.True(Directory.Exists(strayDirectory.FullName));
        }
        finally
        {
            workspaceRoot.Delete(recursive: true);
        }
    }
}
