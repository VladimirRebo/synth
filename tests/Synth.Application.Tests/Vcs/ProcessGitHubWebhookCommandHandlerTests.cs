using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Synth.Application.Cqrs;
using Synth.Application.Indexing;
using Synth.Application.Vcs;
using Synth.Domain.Vcs;
using Synth.Infrastructure.Vcs;

namespace Synth.Application.Tests.Vcs;

// Proves the GitHub webhook -> reindex-on-push flow: signature verification, push-event/branch
// filtering, and collection resolution via RepoUrlInfo's slug (the same one IndexRepositoryCommandHandler
// derives when a repo URL is first indexed). Runs offline: InMemoryRepositoryRegistry stands in for
// the SQLite-backed one, and a fake ICommandHandler<IndexRepositoryCommand, ...> records what would
// have been dispatched instead of actually cloning/indexing anything.
public class ProcessGitHubWebhookCommandHandlerTests
{
    private const string Secret = "test-secret";
    private const string RepoUrl = "https://github.com/owner/repo.git";
    private const string Slug = "github-com-owner-repo";

    [Fact]
    public async Task Missing_signature_is_unauthorized()
    {
        var (handler, _) = CreateHandler();

        var result = await handler.HandleAsync(new ProcessGitHubWebhookCommand("push", null, "{}"));

        Assert.Equal(ProcessGitHubWebhookResult.Kind.Unauthorized, result.Status);
    }

    [Fact]
    public async Task No_secret_configured_is_unauthorized_even_with_a_wellformed_signature()
    {
        var (handler, _) = CreateHandler(secret: null);
        var body = PushBody("refs/heads/main", RepoUrl, "main");

        var result = await handler.HandleAsync(
            new ProcessGitHubWebhookCommand("push", Sign(body, Secret), body));

        Assert.Equal(ProcessGitHubWebhookResult.Kind.Unauthorized, result.Status);
    }

    [Fact]
    public async Task Wrong_signature_is_unauthorized()
    {
        var (handler, _) = CreateHandler();
        var body = PushBody("refs/heads/main", RepoUrl, "main");

        var result = await handler.HandleAsync(
            new ProcessGitHubWebhookCommand("push", Sign(body, "not-the-configured-secret"), body));

        Assert.Equal(ProcessGitHubWebhookResult.Kind.Unauthorized, result.Status);
    }

    [Fact]
    public async Task Non_push_event_is_ignored_and_never_dispatched()
    {
        var (handler, index) = CreateHandler();
        var body = PushBody("refs/heads/main", RepoUrl, "main");

        var result = await handler.HandleAsync(
            new ProcessGitHubWebhookCommand("issues", Sign(body, Secret), body));

        Assert.Equal(ProcessGitHubWebhookResult.Kind.Ignored, result.Status);
        Assert.Null(index.LastCommand);
    }

    [Fact]
    public async Task Tag_push_is_ignored_and_never_dispatched()
    {
        var (handler, index) = CreateHandler();
        var body = PushBody("refs/tags/v1.0.0", RepoUrl, "main");

        var result = await handler.HandleAsync(
            new ProcessGitHubWebhookCommand("push", Sign(body, Secret), body));

        Assert.Equal(ProcessGitHubWebhookResult.Kind.Ignored, result.Status);
        Assert.Null(index.LastCommand);
    }

    [Fact]
    public async Task Push_to_an_unindexed_repository_is_ignored()
    {
        var (handler, index) = CreateHandler(registry: new InMemoryRepositoryRegistry());
        var body = PushBody("refs/heads/main", RepoUrl, "main");

        var result = await handler.HandleAsync(
            new ProcessGitHubWebhookCommand("push", Sign(body, Secret), body));

        Assert.Equal(ProcessGitHubWebhookResult.Kind.Ignored, result.Status);
        Assert.Null(index.LastCommand);
    }

    [Fact]
    public async Task Push_to_a_different_branch_than_the_indexed_one_is_ignored()
    {
        var registry = await SeededRegistry(branch: "main");
        var (handler, index) = CreateHandler(registry: registry);
        var body = PushBody("refs/heads/feature-x", RepoUrl, "main");

        var result = await handler.HandleAsync(
            new ProcessGitHubWebhookCommand("push", Sign(body, Secret), body));

        Assert.Equal(ProcessGitHubWebhookResult.Kind.Ignored, result.Status);
        Assert.Null(index.LastCommand);
    }

    [Fact]
    public async Task Push_to_the_indexed_branch_dispatches_a_reindex()
    {
        var registry = await SeededRegistry(branch: "main");
        var (handler, index) = CreateHandler(registry: registry, indexOutcome: IndexStartOutcome.Started(Slug));
        var body = PushBody("refs/heads/main", RepoUrl, "main");

        var result = await handler.HandleAsync(
            new ProcessGitHubWebhookCommand("push", Sign(body, Secret), body));

        Assert.Equal(ProcessGitHubWebhookResult.Kind.Started, result.Status);
        Assert.NotNull(index.LastCommand);
        Assert.Equal(RepoUrl, index.LastCommand!.RepoUrl);
        Assert.Equal("main", index.LastCommand.Branch);
    }

    [Fact]
    public async Task Push_to_the_default_branch_matches_a_nullbranch_entry_via_the_payload()
    {
        // entry.Branch is null (indexed via "default branch" at the time); the payload's own
        // default_branch is what resolves the match, not a stored branch name.
        var registry = await SeededRegistry(branch: null);
        var (handler, index) = CreateHandler(registry: registry, indexOutcome: IndexStartOutcome.Started(Slug));
        var body = PushBody("refs/heads/main", RepoUrl, defaultBranch: "main");

        var result = await handler.HandleAsync(
            new ProcessGitHubWebhookCommand("push", Sign(body, Secret), body));

        Assert.Equal(ProcessGitHubWebhookResult.Kind.Started, result.Status);
        Assert.NotNull(index.LastCommand);
    }

    [Fact]
    public async Task Already_running_reindex_is_reported_not_dropped_silently()
    {
        var registry = await SeededRegistry(branch: "main");
        var (handler, _) = CreateHandler(registry: registry, indexOutcome: IndexStartOutcome.AlreadyRunning());
        var body = PushBody("refs/heads/main", RepoUrl, "main");

        var result = await handler.HandleAsync(
            new ProcessGitHubWebhookCommand("push", Sign(body, Secret), body));

        Assert.Equal(ProcessGitHubWebhookResult.Kind.AlreadyRunning, result.Status);
    }

    private static async Task<InMemoryRepositoryRegistry> SeededRegistry(string? branch)
    {
        var registry = new InMemoryRepositoryRegistry();
        await registry.UpsertAsync(new RepositoryEntry
        {
            Collection = Slug,
            SourceType = "github",
            Source = RepoUrl,
            Branch = branch,
            LastIndexedAt = DateTime.UtcNow,
            ChunkCount = 10,
        });
        return registry;
    }

    private static (ProcessGitHubWebhookCommandHandler Handler, FakeIndexHandler Index) CreateHandler(
        string? secret = Secret, IRepositoryRegistry? registry = null, IndexStartOutcome? indexOutcome = null)
    {
        var options = new StaticOptionsMonitor<VcsOptions>(new VcsOptions
        {
            GitHub = new VcsOptions.GitHubAuth { WebhookSecret = secret },
        });
        var index = new FakeIndexHandler(indexOutcome ?? IndexStartOutcome.Started(Slug));
        var handler = new ProcessGitHubWebhookCommandHandler(options, registry ?? new InMemoryRepositoryRegistry(), index);
        return (handler, index);
    }

    private static string PushBody(string @ref, string cloneUrl, string defaultBranch) =>
        $$"""
        {
            "ref": "{{@ref}}",
            "repository": {
                "clone_url": "{{cloneUrl}}",
                "default_branch": "{{defaultBranch}}"
            }
        }
        """;

    private static string Sign(string body, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexStringLower(hash);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeIndexHandler(IndexStartOutcome outcome)
        : ICommandHandler<IndexRepositoryCommand, IndexStartOutcome>
    {
        public IndexRepositoryCommand? LastCommand { get; private set; }

        public Task<IndexStartOutcome> HandleAsync(IndexRepositoryCommand command, CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            return Task.FromResult(outcome);
        }
    }
}
