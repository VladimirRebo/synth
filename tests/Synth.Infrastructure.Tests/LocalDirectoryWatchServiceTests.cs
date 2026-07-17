using Microsoft.Extensions.Logging.Abstractions;
using Synth.Application.Cqrs;
using Synth.Application.Indexing;
using Synth.Domain.Vcs;
using Synth.Infrastructure.Vcs;

namespace Synth.Infrastructure.Tests;

// Proves LocalDirectoryWatchService's real behavior against a real filesystem (FileSystemWatcher
// can't be faked away) with short, test-only sync/debounce delays so these stay fast — no real git,
// no real SQLite, no real IndexRepositoryCommandHandler.
public class LocalDirectoryWatchServiceTests : IDisposable
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private string CreateTempDir()
    {
        var dir = Directory.CreateTempSubdirectory("synth-watch-tests-").FullName;
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public async Task Reindexes_a_watched_directory_after_a_file_changes()
    {
        var dir = CreateTempDir();
        var registry = await SeededRegistry(("local-repo", "local", dir));
        var index = new FakeIndexHandler();
        using var service = CreateService(registry, index);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilWatchingAsync();

        File.WriteAllText(Path.Combine(dir, "new.txt"), "hello");

        await WaitForAsync(() => index.CallCount > 0);

        Assert.Equal(dir, index.LastCommand!.Path);
    }

    [Fact]
    public async Task A_burst_of_changes_collapses_into_one_reindex()
    {
        var dir = CreateTempDir();
        var registry = await SeededRegistry(("local-repo", "local", dir));
        var index = new FakeIndexHandler();
        using var service = CreateService(registry, index);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilWatchingAsync();

        for (var i = 0; i < 10; i++)
        {
            File.WriteAllText(Path.Combine(dir, $"file{i}.txt"), "x");
            await Task.Delay(10); // faster than Debounce, so this whole burst is one quiet period
        }

        await WaitForAsync(() => index.CallCount > 0);
        await Task.Delay(Debounce * 3); // give a wrongly-firing second timer a chance to prove itself

        Assert.Equal(1, index.CallCount);
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData(".git")]
    [InlineData("node_modules")]
    public async Task Ignores_changes_under_conventionally_skipped_directories(string ignoredSegment)
    {
        var dir = CreateTempDir();
        var registry = await SeededRegistry(("local-repo", "local", dir));
        var index = new FakeIndexHandler();
        using var service = CreateService(registry, index);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilWatchingAsync();

        var ignoredDir = Directory.CreateDirectory(Path.Combine(dir, ignoredSegment));
        File.WriteAllText(Path.Combine(ignoredDir.FullName, "output.txt"), "generated");

        // Nothing to await for a negative — give it comfortably longer than the debounce delay.
        await Task.Delay(Debounce * 3);

        Assert.Equal(0, index.CallCount);
    }

    [Fact]
    public async Task Non_local_sources_are_never_watched()
    {
        var dir = CreateTempDir();
        var registry = await SeededRegistry(("github-repo", "github", dir));
        var index = new FakeIndexHandler();
        using var service = CreateService(registry, index);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(SyncInterval * 3); // let a sync tick pass — nothing should start watching

        File.WriteAllText(Path.Combine(dir, "new.txt"), "hello");
        await Task.Delay(Debounce * 3);

        Assert.Equal(0, index.CallCount);
    }

    [Fact]
    public async Task Stops_watching_once_the_collection_is_removed_from_the_registry()
    {
        var dir = CreateTempDir();
        var registry = await SeededRegistry(("local-repo", "local", dir));
        var index = new FakeIndexHandler();
        using var service = CreateService(registry, index);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilWatchingAsync();

        await registry.DeleteAsync("local-repo");
        await Task.Delay(SyncInterval * 3); // let a sync tick notice the deletion and stop watching

        File.WriteAllText(Path.Combine(dir, "new.txt"), "hello");
        await Task.Delay(Debounce * 3);

        Assert.Equal(0, index.CallCount);
    }

    [Fact]
    public async Task An_AlreadyRunning_outcome_does_not_throw()
    {
        var dir = CreateTempDir();
        var registry = await SeededRegistry(("local-repo", "local", dir));
        var index = new FakeIndexHandler { NextOutcome = IndexStartOutcome.AlreadyRunning() };
        using var service = CreateService(registry, index);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilWatchingAsync();

        File.WriteAllText(Path.Combine(dir, "new.txt"), "hello");

        await WaitForAsync(() => index.CallCount > 0);
    }

    // Gives the background sync loop time to run at least one tick and attach its FileSystemWatcher
    // before a test starts writing files, avoiding a race against the first tick.
    private static Task WaitUntilWatchingAsync() => Task.Delay(SyncInterval * 3);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition was not met within the timeout.");
            await Task.Delay(20);
        }
    }

    private static async Task<InMemoryRepositoryRegistry> SeededRegistry(
        params (string Collection, string SourceType, string Source)[] entries)
    {
        var registry = new InMemoryRepositoryRegistry();
        foreach (var (collection, sourceType, source) in entries)
        {
            await registry.UpsertAsync(new RepositoryEntry
            {
                Collection = collection,
                SourceType = sourceType,
                Source = source,
                Branch = null,
                LastIndexedAt = DateTime.UtcNow,
                ChunkCount = 1,
            });
        }
        return registry;
    }

    private static LocalDirectoryWatchService CreateService(IRepositoryRegistry registry, FakeIndexHandler index) =>
        new(registry, index, NullLogger<LocalDirectoryWatchService>.Instance,
            registrySyncInterval: SyncInterval, debounceDelay: Debounce);

    private sealed class FakeIndexHandler : ICommandHandler<IndexRepositoryCommand, IndexStartOutcome>
    {
        public IndexRepositoryCommand? LastCommand { get; private set; }
        public int CallCount { get; private set; }
        public IndexStartOutcome? NextOutcome { get; set; }

        public Task<IndexStartOutcome> HandleAsync(IndexRepositoryCommand command, CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            CallCount++;
            return Task.FromResult(NextOutcome ?? IndexStartOutcome.Started(command.Path ?? command.RepoUrl!));
        }
    }
}
