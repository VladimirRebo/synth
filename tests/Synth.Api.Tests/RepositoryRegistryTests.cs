using Synth.Api.Vcs;

namespace Synth.Api.Tests;

// Round-trip coverage of the registry contract against the in-memory implementation (the
// production fallback used when no Mongo is configured, and the same shape MongoRepositoryRegistry
// exposes). No live Mongo required — matching how this repo tests its Mongo-backed pieces.
public class RepositoryRegistryTests
{
    [Fact]
    public async Task Upsert_then_list_round_trips_the_entry()
    {
        var registry = new InMemoryRepositoryRegistry();
        var entry = new RepositoryEntry
        {
            Collection = "github-com-owner-repo",
            SourceType = "github",
            Source = "https://github.com/owner/repo.git",
            Branch = "main",
            LastIndexedAt = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc),
            ChunkCount = 42,
        };

        await registry.UpsertAsync(entry);

        var listed = await registry.ListAsync();

        var found = Assert.Single(listed);
        Assert.Equal(entry, found);
    }

    [Fact]
    public async Task Upsert_replaces_the_entry_for_the_same_collection()
    {
        var registry = new InMemoryRepositoryRegistry();
        var collection = "default";

        await registry.UpsertAsync(new RepositoryEntry
        {
            Collection = collection,
            SourceType = "local",
            Source = "/tmp/first",
            LastIndexedAt = DateTime.UtcNow,
            ChunkCount = 1,
        });
        await registry.UpsertAsync(new RepositoryEntry
        {
            Collection = collection,
            SourceType = "local",
            Source = "/tmp/second",
            LastIndexedAt = DateTime.UtcNow,
            ChunkCount = 9,
        });

        var listed = await registry.ListAsync();

        var found = Assert.Single(listed);
        Assert.Equal("/tmp/second", found.Source);
        Assert.Equal(9, found.ChunkCount);
    }

    [Fact]
    public async Task List_is_empty_before_any_upsert()
    {
        var registry = new InMemoryRepositoryRegistry();

        Assert.Empty(await registry.ListAsync());
    }

    [Fact]
    public async Task Delete_removes_the_entry_and_reports_true()
    {
        var registry = new InMemoryRepositoryRegistry();
        await registry.UpsertAsync(new RepositoryEntry
        {
            Collection = "default",
            SourceType = "local",
            Source = "/tmp/repo",
            LastIndexedAt = DateTime.UtcNow,
            ChunkCount = 3,
        });

        var removed = await registry.DeleteAsync("default");

        Assert.True(removed);
        Assert.Empty(await registry.ListAsync());
    }

    [Fact]
    public async Task Delete_of_an_unknown_collection_is_a_noop_and_reports_false()
    {
        var registry = new InMemoryRepositoryRegistry();

        Assert.False(await registry.DeleteAsync("never-indexed"));
    }
}
