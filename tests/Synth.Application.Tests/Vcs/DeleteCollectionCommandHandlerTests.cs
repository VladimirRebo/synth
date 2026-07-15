using Synth.Application.Vcs;
using Synth.Domain.Graph;
using Synth.Domain.Vcs;
using Synth.Infrastructure.Graph;
using Synth.Infrastructure.Storage;
using Synth.Infrastructure.Vcs;

namespace Synth.Application.Tests.Vcs;

// Proves SYNTH-67: the collection-delete sequence (vector-store collection + call-graph edges +
// registry entry, plus the cloned-remote checkout cleanup gated on SourceType) now lives in
// DeleteCollectionCommandHandler, unchanged in behavior from the old
// RepositoryEndpoints.DeleteCollectionAsync + its inline IsClonedRemote check. Runs offline against
// in-memory stores and a fake IGitRepoService (no real git), mirroring IndexRepositoryCommandHandlerTests.
public class DeleteCollectionCommandHandlerTests
{
    private const string Collection = "repo-a";

    // Records the collection whose checkout was asked to be removed, so the SourceType gate can be
    // asserted without touching the filesystem.
    private sealed class FakeGitRepoService : IGitRepoService
    {
        public string? RemovedCheckoutSlug { get; private set; }

        public Task<string> EnsureRepoAsync(string repoUrl, string? branch = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public void RemoveCheckout(string slug) => RemovedCheckoutSlug = slug;

        public string ResolveCheckoutPath(string slug) => slug;

        public Task<string?> GetRemoteHeadShaAsync(string repoUrl, string? branch = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private static async Task<InMemoryRepositoryRegistry> SeededRegistry(
        params (string Collection, string SourceType)[] collections)
    {
        var registry = new InMemoryRepositoryRegistry();
        foreach (var (collection, sourceType) in collections)
        {
            await registry.UpsertAsync(new RepositoryEntry
            {
                Collection = collection,
                SourceType = sourceType,
                Source = $"/tmp/{collection}",
                LastIndexedAt = DateTime.UtcNow,
                ChunkCount = 1,
            });
        }

        return registry;
    }

    private static DeleteCollectionCommandHandler CreateHandler(
        IRepositoryRegistry registry,
        ICodeGraphStore? graphStore = null,
        IGitRepoService? git = null) =>
        new(
            new LocalCodeChunkStore(),
            graphStore ?? new InMemoryCodeGraphStore(),
            registry,
            git ?? new FakeGitRepoService());

    [Fact]
    public async Task Handle_removes_entry_clears_graph_and_reports_true()
    {
        var registry = await SeededRegistry((Collection, "local"), ("repo-b", "local"));
        var graphStore = new InMemoryCodeGraphStore();
        // Seed a call-graph edge so we can prove the handler clears it.
        await graphStore.ReplaceEdgesAsync(Collection, [new CallEdge(Collection, "A.M", "B.N", "A.cs", 1)]);
        var handler = CreateHandler(registry, graphStore);

        var removed = await handler.HandleAsync(new DeleteCollectionCommand(Collection));

        Assert.True(removed);

        // Registry entry gone, siblings untouched.
        var remaining = await registry.ListAsync();
        Assert.DoesNotContain(remaining, e => e.Collection == Collection);
        Assert.Contains(remaining, e => e.Collection == "repo-b");

        // Call-graph edges for the collection were cleared (ReplaceEdgesAsync with []).
        Assert.Empty(await graphStore.FindCalleesAsync(Collection, "A.M"));
    }

    [Fact]
    public async Task Handle_of_an_unknown_collection_reports_false()
    {
        var registry = await SeededRegistry((Collection, "local"));
        var handler = CreateHandler(registry);

        var removed = await handler.HandleAsync(new DeleteCollectionCommand("never-indexed"));

        Assert.False(removed);
    }

    [Theory]
    [InlineData("github")]
    [InlineData("gitlab")]
    public async Task Handle_of_a_cloned_remote_removes_its_on_disk_checkout(string sourceType)
    {
        var registry = await SeededRegistry(("remote-repo", sourceType));
        var git = new FakeGitRepoService();
        var handler = CreateHandler(registry, git: git);

        var removed = await handler.HandleAsync(new DeleteCollectionCommand("remote-repo"));

        Assert.True(removed);
        Assert.Equal("remote-repo", git.RemovedCheckoutSlug);
    }

    [Fact]
    public async Task Handle_of_a_local_collection_removes_no_checkout()
    {
        var registry = await SeededRegistry(("local-repo", "local"));
        var git = new FakeGitRepoService();
        var handler = CreateHandler(registry, git: git);

        var removed = await handler.HandleAsync(new DeleteCollectionCommand("local-repo"));

        Assert.True(removed);
        // A local source is never cloned, so the checkout-removal path must be skipped entirely.
        Assert.Null(git.RemovedCheckoutSlug);
    }

    [Fact]
    public async Task Handle_of_an_unknown_cloned_looking_collection_removes_no_checkout()
    {
        // Nothing in the registry: the delete reports false, and since no entry was found the
        // SourceType-gated checkout cleanup must not run (removed is false anyway).
        var registry = new InMemoryRepositoryRegistry();
        var git = new FakeGitRepoService();
        var handler = CreateHandler(registry, git: git);

        var removed = await handler.HandleAsync(new DeleteCollectionCommand("ghost"));

        Assert.False(removed);
        Assert.Null(git.RemovedCheckoutSlug);
    }
}
