using Synth.Infrastructure;
using Synth.Infrastructure.Vcs;

namespace Synth.Infrastructure.Tests;

// Round-trip coverage of SqlitePollStateStore against a real temp-file SQLite database — same
// approach as SqliteRepositoryRegistryTests. Each test gets a throwaway db file, never touches
// ~/.synth/synth.db.
public class SqlitePollStateStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteConnectionFactory _factory;

    public SqlitePollStateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "synth-tests", Guid.NewGuid().ToString("N"));
        _factory = new SqliteConnectionFactory(Path.Combine(_tempDir, "synth.db"));
    }

    [Fact]
    public async Task GetLastKnownShaAsync_returns_null_for_a_collection_never_polled()
    {
        var store = new SqlitePollStateStore(_factory);

        Assert.Null(await store.GetLastKnownShaAsync("github-com-owner-repo"));
    }

    [Fact]
    public async Task Set_then_get_round_trips_the_sha()
    {
        var store = new SqlitePollStateStore(_factory);

        await store.SetLastKnownShaAsync("github-com-owner-repo", "abc123");

        Assert.Equal("abc123", await store.GetLastKnownShaAsync("github-com-owner-repo"));
    }

    [Fact]
    public async Task Setting_again_overwrites_the_previous_sha()
    {
        var store = new SqlitePollStateStore(_factory);

        await store.SetLastKnownShaAsync("github-com-owner-repo", "abc123");
        await store.SetLastKnownShaAsync("github-com-owner-repo", "def456");

        Assert.Equal("def456", await store.GetLastKnownShaAsync("github-com-owner-repo"));
    }

    [Fact]
    public async Task Different_collections_are_tracked_independently()
    {
        var store = new SqlitePollStateStore(_factory);

        await store.SetLastKnownShaAsync("repo-a", "sha-a");
        await store.SetLastKnownShaAsync("repo-b", "sha-b");

        Assert.Equal("sha-a", await store.GetLastKnownShaAsync("repo-a"));
        Assert.Equal("sha-b", await store.GetLastKnownShaAsync("repo-b"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
