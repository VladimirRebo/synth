using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Synth.Application.Cqrs;
using Synth.Application.Indexing;
using Synth.Application.Vcs;
using Synth.Domain.Vcs;
using Synth.Infrastructure.Vcs;

namespace Synth.Infrastructure.Tests;

// Proves RepositoryPollingService's single-tick logic (PollOnceAsync) against fakes — no real git, no
// real SQLite, no real IndexRepositoryCommandHandler. GetRemoteHeadShaAsync/IRepositoryPollState/the
// index dispatch are each faked so the poll/compare/dispatch decision is asserted in isolation; the
// SHA-resolution mechanism itself is covered by GitRepoServiceTests, and the SQLite persistence by
// SqlitePollStateStoreTests.
public class RepositoryPollingServiceTests
{
    private const string Collection = "github-com-owner-repo";
    private const string RepoUrl = "https://github.com/owner/repo.git";

    [Fact]
    public async Task Local_sources_are_never_checked()
    {
        var registry = await SeededRegistry(("local-repo", "local", "/some/path", null));
        var git = new FakeGitRepoService();
        var (service, _) = CreateService(registry, git);

        await service.PollOnceAsync(CancellationToken.None);

        Assert.Empty(git.RequestedUrls);
    }

    [Fact]
    public async Task First_observation_records_a_baseline_without_reindexing()
    {
        var registry = await SeededRegistry((Collection, "github", RepoUrl, "main"));
        var git = new FakeGitRepoService { ShaByUrl = { [RepoUrl] = "sha-1" } };
        var (service, index) = CreateService(registry, git);

        await service.PollOnceAsync(CancellationToken.None);

        Assert.Null(index.LastCommand); // already indexed at sha-1 when it was first indexed
    }

    [Fact]
    public async Task Unchanged_sha_on_a_later_tick_does_not_reindex()
    {
        var registry = await SeededRegistry((Collection, "github", RepoUrl, "main"));
        var git = new FakeGitRepoService { ShaByUrl = { [RepoUrl] = "sha-1" } };
        var (service, index) = CreateService(registry, git);

        await service.PollOnceAsync(CancellationToken.None); // records the baseline
        await service.PollOnceAsync(CancellationToken.None); // same SHA again

        Assert.Null(index.LastCommand);
    }

    [Fact]
    public async Task A_new_sha_on_a_later_tick_dispatches_a_reindex()
    {
        var registry = await SeededRegistry((Collection, "github", RepoUrl, "main"));
        var git = new FakeGitRepoService { ShaByUrl = { [RepoUrl] = "sha-1" } };
        var (service, index) = CreateService(registry, git);

        await service.PollOnceAsync(CancellationToken.None); // baseline: sha-1
        git.ShaByUrl[RepoUrl] = "sha-2"; // a new commit landed upstream
        await service.PollOnceAsync(CancellationToken.None);

        Assert.NotNull(index.LastCommand);
        Assert.Equal(RepoUrl, index.LastCommand!.RepoUrl);
        Assert.Equal("main", index.LastCommand.Branch);
    }

    [Fact]
    public async Task An_unresolvable_repo_does_not_stop_the_rest_of_the_tick()
    {
        var registry = await SeededRegistry(
            ("repo-a", "github", "https://github.com/a/a.git", "main"),
            ("repo-b", "github", "https://github.com/b/b.git", "main"));
        var git = new FakeGitRepoService
        {
            ThrowForUrl = "https://github.com/a/a.git",
            ShaByUrl = { ["https://github.com/b/b.git"] = "sha-1" },
        };
        var (service, _) = CreateService(registry, git);

        // Must not throw despite repo-a failing — repo-b is still reachable and gets recorded.
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Equal("sha-1", await git.LastPollState!.GetLastKnownShaAsync("repo-b"));
    }

    [Fact]
    public async Task An_unknown_branch_is_skipped_without_recording_state()
    {
        var registry = await SeededRegistry((Collection, "github", RepoUrl, "main"));
        var git = new FakeGitRepoService(); // ShaByUrl has no entry -> null, simulating an unknown ref
        var (service, index) = CreateService(registry, git);

        await service.PollOnceAsync(CancellationToken.None);

        Assert.Null(index.LastCommand);
        Assert.Null(await git.LastPollState!.GetLastKnownShaAsync(Collection));
    }

    [Fact]
    public async Task AlreadyRunning_outcome_does_not_throw()
    {
        var registry = await SeededRegistry((Collection, "github", RepoUrl, "main"));
        var git = new FakeGitRepoService { ShaByUrl = { [RepoUrl] = "sha-1" } };
        var (service, index) = CreateService(registry, git, IndexStartOutcome.Started(Collection));
        await service.PollOnceAsync(CancellationToken.None); // baseline

        git.ShaByUrl[RepoUrl] = "sha-2";
        index.NextOutcome = IndexStartOutcome.AlreadyRunning();

        await Record.ExceptionAsync(() => service.PollOnceAsync(CancellationToken.None));
    }

    private static async Task<InMemoryRepositoryRegistry> SeededRegistry(
        params (string Collection, string SourceType, string Source, string? Branch)[] entries)
    {
        var registry = new InMemoryRepositoryRegistry();
        foreach (var (collection, sourceType, source, branch) in entries)
        {
            await registry.UpsertAsync(new RepositoryEntry
            {
                Collection = collection,
                SourceType = sourceType,
                Source = source,
                Branch = branch,
                LastIndexedAt = DateTime.UtcNow,
                ChunkCount = 1,
            });
        }
        return registry;
    }

    private static (RepositoryPollingService Service, FakeIndexHandler Index) CreateService(
        IRepositoryRegistry registry, FakeGitRepoService git, IndexStartOutcome? indexOutcome = null)
    {
        var pollState = new InMemoryPollState();
        git.LastPollState = pollState;
        var index = new FakeIndexHandler(indexOutcome ?? IndexStartOutcome.Started(Collection));
        var options = new StaticOptionsMonitor<VcsOptions>(new VcsOptions { PollIntervalMinutes = 5 });
        var service = new RepositoryPollingService(
            registry, git, pollState, index, options, NullLogger<RepositoryPollingService>.Instance);
        return (service, index);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class InMemoryPollState : IRepositoryPollState
    {
        private readonly Dictionary<string, string> _byCollection = new(StringComparer.Ordinal);

        public Task<string?> GetLastKnownShaAsync(string collection, CancellationToken cancellationToken = default) =>
            Task.FromResult(_byCollection.TryGetValue(collection, out var sha) ? sha : null);

        public Task SetLastKnownShaAsync(string collection, string sha, CancellationToken cancellationToken = default)
        {
            _byCollection[collection] = sha;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGitRepoService : IGitRepoService
    {
        public Dictionary<string, string> ShaByUrl { get; } = new(StringComparer.Ordinal);
        public string? ThrowForUrl { get; set; }
        public List<string> RequestedUrls { get; } = [];
        public IRepositoryPollState? LastPollState { get; set; }

        public Task<string> EnsureRepoAsync(string repoUrl, string? branch = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by the polling flow.");

        public void RemoveCheckout(string slug) { }

        public string ResolveCheckoutPath(string slug) => slug;

        public Task<string?> GetRemoteHeadShaAsync(string repoUrl, string? branch = null, CancellationToken cancellationToken = default)
        {
            RequestedUrls.Add(repoUrl);
            if (repoUrl == ThrowForUrl)
                throw new InvalidOperationException("simulated ls-remote failure");

            return Task.FromResult(ShaByUrl.TryGetValue(repoUrl, out var sha) ? sha : null);
        }
    }

    private sealed class FakeIndexHandler(IndexStartOutcome outcome) : ICommandHandler<IndexRepositoryCommand, IndexStartOutcome>
    {
        public IndexRepositoryCommand? LastCommand { get; private set; }
        public IndexStartOutcome? NextOutcome { get; set; }

        public Task<IndexStartOutcome> HandleAsync(IndexRepositoryCommand command, CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            var result = NextOutcome ?? outcome;
            return Task.FromResult(result);
        }
    }
}
