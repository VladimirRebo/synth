using Microsoft.Extensions.Logging.Abstractions;
using Synth.Api.Vcs;
using Synth.Core.Vcs;
using Synth.Domain.Vcs;

namespace Synth.Api.Tests;

// SYNTH-45: the startup sweep removes workspace-root subdirectories with no matching registry entry
// while leaving matching ones alone, and no-ops when the workspace root doesn't exist yet. Driven
// against a temp workspace root — never the real ~/.synth/workspaces — via GitRepoService's
// Vcs:WorkspaceRoot option.
public sealed class OrphanCheckoutSweeperTests : IDisposable
{
    private readonly string _workspaceRoot;

    public OrphanCheckoutSweeperTests() =>
        _workspaceRoot = Directory.CreateTempSubdirectory("synth-sweeper-tests-").FullName;

    private OrphanCheckoutSweeper NewSweeper(IRepositoryRegistry registry)
    {
        var git = new GitRepoService(
            new MutableOptionsMonitor<VcsOptions>(new VcsOptions { WorkspaceRoot = _workspaceRoot }));
        return new OrphanCheckoutSweeper(registry, git, NullLogger<OrphanCheckoutSweeper>.Instance);
    }

    private static async Task<InMemoryRepositoryRegistry> RegistryWith(params string[] collections)
    {
        var registry = new InMemoryRepositoryRegistry();
        foreach (var collection in collections)
        {
            await registry.UpsertAsync(new RepositoryEntry
            {
                Collection = collection,
                SourceType = "github",
                Source = $"https://example.com/acme/{collection}.git",
                LastIndexedAt = DateTime.UtcNow,
                ChunkCount = 1,
            });
        }

        return registry;
    }

    [Fact]
    public async Task Sweep_removes_orphan_directories_and_keeps_matching_ones()
    {
        var kept = Directory.CreateDirectory(Path.Combine(_workspaceRoot, "known-repo"));
        var orphan = Directory.CreateDirectory(Path.Combine(_workspaceRoot, "orphan-repo"));
        var registry = await RegistryWith("known-repo");

        await NewSweeper(registry).SweepAsync(CancellationToken.None);

        Assert.True(Directory.Exists(kept.FullName));
        Assert.False(Directory.Exists(orphan.FullName));
    }

    [Fact]
    public async Task Sweep_is_a_noop_when_the_workspace_root_does_not_exist()
    {
        Directory.Delete(_workspaceRoot, recursive: true);
        var registry = await RegistryWith("known-repo");

        // Must not throw even though the workspace root is absent (fresh install, nothing indexed).
        await NewSweeper(registry).SweepAsync(CancellationToken.None);

        Assert.False(Directory.Exists(_workspaceRoot));
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceRoot))
            Directory.Delete(_workspaceRoot, recursive: true);
    }
}
