using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Synth.Api.Mcp;
using Synth.Application.Vcs;
using Synth.Domain;
using Synth.Domain.Graph;
using Synth.Domain.Vcs;
using Synth.Infrastructure.Graph;
using Synth.Infrastructure.Storage;
using Synth.Infrastructure.Vcs;

namespace Synth.Api.Tests;

// Proves SYNTH-43 (part 2): the `delete_collection` MCP tool drives the exact same removal sequence
// (vector-store collection, call-graph edges, registry entry) that DELETE /repositories/{collection}
// uses — both now go through the shared DeleteCollectionCommandHandler (SYNTH-67 moved it behind the
// CQRS seam) — and the tool mirrors its 204/404 split via DeleteCollectionResult.Deleted. Runs offline
// against in-memory stores.
public class DeleteCollectionMcpToolTests
{
    private const string Collection = "repo-a";

    // Cloned-remote checkout cleanup is exercised in DeleteCollectionCommandHandlerTests; these MCP
    // tests only care about the deleted/not-deleted result, so a no-op git service suffices.
    private sealed class NoopGitRepoService : IGitRepoService
    {
        public Task<string> EnsureRepoAsync(string repoUrl, string? branch = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public void RemoveCheckout(string slug) { }

        public string ResolveCheckoutPath(string slug) => slug;
    }

    private static DeleteCollectionCommandHandler CreateHandler(
        IRepositoryRegistry registry,
        ICodeChunkStore? chunkStore = null,
        ICodeGraphStore? graphStore = null) =>
        new(
            chunkStore ?? new LocalCodeChunkStore(),
            graphStore ?? new InMemoryCodeGraphStore(),
            registry,
            new NoopGitRepoService());

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
    public async Task Delete_removes_the_entry_and_reports_deleted()
    {
        var registry = await SeededRegistry(Collection, "repo-b");
        var chunkStore = new LocalCodeChunkStore();
        var graphStore = new InMemoryCodeGraphStore();
        // Seed a call-graph edge so we can prove the tool clears it via the shared logic.
        await graphStore.ReplaceEdgesAsync(Collection, [new CallEdge(Collection, "A.M", "B.N", "A.cs", 1)]);

        var result = await DeleteCollectionTool.DeleteCollectionAsync(
            CreateHandler(registry, chunkStore, graphStore), Collection);

        Assert.True(result.Deleted);
        Assert.Equal(Collection, result.Collection);

        // Registry entry gone, siblings untouched.
        var remaining = await registry.ListAsync();
        Assert.DoesNotContain(remaining, e => e.Collection == Collection);
        Assert.Contains(remaining, e => e.Collection == "repo-b");

        // Call-graph edges for the collection were cleared (ReplaceEdgesAsync with []).
        Assert.Empty(await graphStore.FindCalleesAsync(Collection, "A.M"));
    }

    [Fact]
    public async Task Delete_of_an_unknown_collection_reports_not_deleted()
    {
        var registry = await SeededRegistry(Collection);

        var result = await DeleteCollectionTool.DeleteCollectionAsync(
            CreateHandler(registry), "never-indexed");

        Assert.False(result.Deleted);
        Assert.Equal("never-indexed", result.Collection);
    }

    [Fact]
    public async Task Delete_rejects_a_blank_collection()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => DeleteCollectionTool.DeleteCollectionAsync(
            CreateHandler(new InMemoryRepositoryRegistry()), "  "));
    }

    [Fact]
    public void Delete_collection_tool_is_registered_on_the_mcp_server()
    {
        using var factory = new WebApplicationFactory<Program>();

        var tools = factory.Services.GetServices<McpServerTool>();

        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "delete_collection");
    }
}
