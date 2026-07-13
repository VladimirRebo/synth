using Synth.Domain.Vcs;
using Synth.Infrastructure;
using Synth.Infrastructure.Vcs;

namespace Synth.Infrastructure.Tests;

// Round-trip coverage of SqliteRepositoryRegistry against a real temp-file SQLite database, so we
// exercise the actual SQL (table creation, ON CONFLICT upsert, delete, list) rather than mocks.
// Each test gets a throwaway db file that is deleted afterward — never touches ~/.synth/synth.db.
public class SqliteRepositoryRegistryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteConnectionFactory _factory;

    public SqliteRepositoryRegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "synth-tests", Guid.NewGuid().ToString("N"));
        _factory = new SqliteConnectionFactory(Path.Combine(_tempDir, "synth.db"));
    }

    [Fact]
    public async Task Upsert_then_list_round_trips_the_entry()
    {
        var registry = new SqliteRepositoryRegistry(_factory);
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
    public async Task Upsert_persists_a_null_branch_for_local_sources()
    {
        var registry = new SqliteRepositoryRegistry(_factory);
        var entry = new RepositoryEntry
        {
            Collection = "local-repo",
            SourceType = "local",
            Source = "/tmp/repo",
            Branch = null,
            LastIndexedAt = new DateTime(2026, 7, 8, 9, 30, 0, DateTimeKind.Utc),
            ChunkCount = 7,
        };

        await registry.UpsertAsync(entry);

        var found = Assert.Single(await registry.ListAsync());
        Assert.Null(found.Branch);
        Assert.Equal(entry, found);
    }

    [Fact]
    public async Task Upsert_replaces_the_entry_for_the_same_collection()
    {
        var registry = new SqliteRepositoryRegistry(_factory);
        const string collection = "default";

        await registry.UpsertAsync(new RepositoryEntry
        {
            Collection = collection,
            SourceType = "local",
            Source = "/tmp/first",
            LastIndexedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            ChunkCount = 1,
        });
        await registry.UpsertAsync(new RepositoryEntry
        {
            Collection = collection,
            SourceType = "local",
            Source = "/tmp/second",
            LastIndexedAt = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc),
            ChunkCount = 9,
        });

        var found = Assert.Single(await registry.ListAsync());
        Assert.Equal("/tmp/second", found.Source);
        Assert.Equal(9, found.ChunkCount);
    }

    [Fact]
    public async Task List_is_empty_before_any_upsert()
    {
        var registry = new SqliteRepositoryRegistry(_factory);

        Assert.Empty(await registry.ListAsync());
    }

    [Fact]
    public async Task Delete_removes_the_entry_and_reports_true()
    {
        var registry = new SqliteRepositoryRegistry(_factory);
        await registry.UpsertAsync(new RepositoryEntry
        {
            Collection = "default",
            SourceType = "local",
            Source = "/tmp/repo",
            LastIndexedAt = new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc),
            ChunkCount = 3,
        });

        var removed = await registry.DeleteAsync("default");

        Assert.True(removed);
        Assert.Empty(await registry.ListAsync());
    }

    [Fact]
    public async Task Delete_of_an_unknown_collection_is_a_noop_and_reports_false()
    {
        var registry = new SqliteRepositoryRegistry(_factory);

        Assert.False(await registry.DeleteAsync("never-indexed"));
    }

    [Fact]
    public async Task Full_lifecycle_upsert_list_delete_list_against_a_real_file()
    {
        var registry = new SqliteRepositoryRegistry(_factory);
        var entry = new RepositoryEntry
        {
            Collection = "gitlab-com-team-svc",
            SourceType = "gitlab",
            Source = "https://gitlab.com/team/svc.git",
            Branch = "develop",
            LastIndexedAt = new DateTime(2026, 7, 4, 18, 15, 0, DateTimeKind.Utc),
            ChunkCount = 128,
        };

        await registry.UpsertAsync(entry);
        Assert.Equal(entry, Assert.Single(await registry.ListAsync()));

        Assert.True(await registry.DeleteAsync(entry.Collection));
        Assert.Empty(await registry.ListAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
