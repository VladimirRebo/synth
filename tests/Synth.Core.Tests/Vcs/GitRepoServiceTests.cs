using System.Diagnostics;
using Synth.Core.Vcs;

namespace Synth.Core.Tests.Vcs;

// SYNTH-18: clone/fetch behaviour proven against a real local git fixture reached over a file://
// URL — no network access to github.com/gitlab.com. Each test builds a bare "origin" and an
// authoring working copy in its own temp directory, then drives GitRepoService against it.
public sealed class GitRepoServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _originPath;
    private readonly string _originUrl;
    private readonly string _authoring;
    private readonly string _workspaceRoot;

    public GitRepoServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "synth-git-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _originPath = Path.Combine(_root, "origin.git");
        _originUrl = new Uri(_originPath).AbsoluteUri; // file:///...
        _authoring = Path.Combine(_root, "authoring");
        _workspaceRoot = Path.Combine(_root, "workspaces");

        Git(_root, "init", "--bare", "-b", "main", _originPath);
        Git(_root, "clone", _originUrl, _authoring);
        CommitFile("README.md", "v1");
        Git(_authoring, "push", "-u", "origin", "main");
    }

    private GitRepoService NewService() =>
        new(new StaticOptionsMonitor<VcsOptions>(new VcsOptions { WorkspaceRoot = _workspaceRoot }));

    [Fact]
    public async Task EnsureRepoAsync_clones_on_first_call_into_the_slug_directory()
    {
        var service = NewService();

        var checkout = await service.EnsureRepoAsync(_originUrl, branch: null);

        var expected = Path.Combine(_workspaceRoot, RepoUrlInfo.Parse(_originUrl).Slug);
        Assert.Equal(expected, checkout);
        Assert.True(Directory.Exists(Path.Combine(checkout, ".git")));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(checkout, "README.md")));
    }

    [Fact]
    public async Task EnsureRepoAsync_picks_up_new_commits_on_the_second_call()
    {
        var service = NewService();

        var checkout = await service.EnsureRepoAsync(_originUrl, branch: null);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(checkout, "README.md")));

        // A new commit lands upstream after the initial clone.
        CommitFile("README.md", "v2");
        CommitFile("added.txt", "brand new");
        Git(_authoring, "push", "origin", "main");

        var again = await service.EnsureRepoAsync(_originUrl, branch: null);

        Assert.Equal(checkout, again); // same slug directory, refreshed in place.
        Assert.Equal("v2", File.ReadAllText(Path.Combine(again, "README.md")));
        Assert.Equal("brand new", File.ReadAllText(Path.Combine(again, "added.txt")));
    }

    [Fact]
    public async Task EnsureRepoAsync_hard_resets_local_drift_back_to_the_remote()
    {
        var service = NewService();
        var checkout = await service.EnsureRepoAsync(_originUrl, branch: null);

        // Local uncommitted edit must be discarded by the fetch + reset --hard refresh.
        File.WriteAllText(Path.Combine(checkout, "README.md"), "local scribble");

        await service.EnsureRepoAsync(_originUrl, branch: null);

        Assert.Equal("v1", File.ReadAllText(Path.Combine(checkout, "README.md")));
    }

    [Fact]
    public async Task EnsureRepoAsync_checks_out_the_requested_branch()
    {
        // A feature branch exists only upstream, with a file main doesn't have.
        Git(_authoring, "checkout", "-b", "feature");
        CommitFile("feature.txt", "on feature");
        Git(_authoring, "push", "-u", "origin", "feature");
        Git(_authoring, "checkout", "main");

        var service = NewService();
        var checkout = await service.EnsureRepoAsync(_originUrl, branch: "feature");

        Assert.True(File.Exists(Path.Combine(checkout, "feature.txt")));
        Assert.Equal("on feature", File.ReadAllText(Path.Combine(checkout, "feature.txt")));
    }

    [Fact]
    public async Task EnsureRepoAsync_works_for_a_public_repo_with_no_token_configured()
    {
        // Default VcsOptions: no GitHub/GitLab token. file:// clone must still succeed.
        var service = new GitRepoService(
            new StaticOptionsMonitor<VcsOptions>(new VcsOptions { WorkspaceRoot = _workspaceRoot }));

        var checkout = await service.EnsureRepoAsync(_originUrl, branch: null);

        Assert.True(File.Exists(Path.Combine(checkout, "README.md")));
    }

    [Fact]
    public async Task EnsureRepoAsync_serializes_concurrent_calls_for_the_same_repo()
    {
        // Regression test: EnsureRepoAsync used to have no per-checkout locking, so concurrent calls
        // for the same repo URL could interleave the delete-leftover-directory / clone / fetch+reset
        // steps against the same checkout path. Fire several at once and require every one to
        // succeed and agree on the same, valid checkout — rather than racing into a corrupt state.
        var service = NewService();

        var results = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => service.EnsureRepoAsync(_originUrl, branch: null)));

        Assert.All(results, checkout => Assert.Equal(results[0], checkout));
        Assert.True(Directory.Exists(Path.Combine(results[0], ".git")));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(results[0], "README.md")));
    }

    private void CommitFile(string relativePath, string content)
    {
        File.WriteAllText(Path.Combine(_authoring, relativePath), content);
        Git(_authoring, "add", relativePath);
        Git(_authoring, "commit", "-m", $"set {relativePath}");
    }

    // Runs git in the given directory with a throwaway identity so no global git config is needed.
    private static void Git(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var config in new[]
                 {
                     "-c", "user.email=test@synth.local",
                     "-c", "user.name=Synth Test",
                     "-c", "commit.gpgsign=false",
                     "-c", "init.defaultBranch=main",
                 })
        {
            startInfo.ArgumentList.Add(config);
        }

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp fixture.
        }
    }
}
