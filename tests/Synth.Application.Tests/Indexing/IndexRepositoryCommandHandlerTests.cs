using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Synth.Application;
using Synth.Application.Indexing;
using Synth.Application.Vcs;
using Synth.Chunkers.CSharp;
using Synth.Domain;
using Synth.Domain.Graph;
using Synth.Domain.Vcs;
using Synth.Infrastructure.Storage;
using Synth.Infrastructure.Vcs;

namespace Synth.Application.Tests;

// Proves SYNTH-61: the indexing "try to start" flow (validation + single-slot reservation +
// fire-and-forget dispatch) now lives in IndexRepositoryCommandHandler, unchanged in behavior from
// the old IndexingEndpoints.StartIndexing. Runs offline — a deterministic fake embedding generator
// (no Ollama) and a fake IGitRepoService (no real git) stand in, mirroring IndexingPipelineTests.
public class IndexRepositoryCommandHandlerTests : IDisposable
{
    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private const int Dimensions = 8;

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(new float[Dimensions]))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    // Minimal in-memory graph store so the pipeline's stage-2 output has somewhere to go without
    // coupling this test to the production InMemoryCodeGraphStore.
    private sealed class FakeCodeGraphStore : ICodeGraphStore
    {
        private readonly ConcurrentDictionary<string, List<CallEdge>> _byCollection = new();

        public Task ReplaceEdgesAsync(string collection, IReadOnlyList<CallEdge> edges, CancellationToken ct = default)
        {
            _byCollection[collection] = [.. edges];
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CallEdge>> FindCallersAsync(string collection, string symbol, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CallEdge>>([]);

        public Task<IReadOnlyList<CallEdge>> FindCalleesAsync(string collection, string symbol, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CallEdge>>([]);
    }

    // Stands in for GitRepoService: records the requested URL/branch and returns a pre-populated
    // local checkout directory, so the repoUrl branch can be exercised without cloning anything.
    private sealed class FakeGitRepoService : IGitRepoService
    {
        private readonly string _checkoutPath;

        public FakeGitRepoService(string checkoutPath) => _checkoutPath = checkoutPath;

        public string? RequestedUrl { get; private set; }
        public string? RequestedBranch { get; private set; }

        public Task<string> EnsureRepoAsync(string repoUrl, string? branch = null, CancellationToken cancellationToken = default)
        {
            RequestedUrl = repoUrl;
            RequestedBranch = branch;
            return Task.FromResult(_checkoutPath);
        }

        // Not exercised by the indexing flow (only DeleteCollectionCommandHandler removes checkouts).
        public void RemoveCheckout(string slug) { }

        public string ResolveCheckoutPath(string slug) => slug;

        public Task<string?> GetRemoteHeadShaAsync(string repoUrl, string? branch = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private readonly string _root;
    private readonly InMemoryIndexJobTracker _tracker = new();
    private readonly InMemoryRepositoryRegistry _registry = new();

    public IndexRepositoryCommandHandlerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "synth-index-cmd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private IndexRepositoryCommandHandler CreateHandler(IGitRepoService? git = null)
    {
        var pipeline = new IndexingPipeline(
            [new CSharpRoslynChunker()],
            new FakeEmbeddingGenerator(),
            new LocalCodeChunkStore(),
            new FakeCodeGraphStore());

        return new IndexRepositoryCommandHandler(
            pipeline,
            git ?? new FakeGitRepoService(_root),
            _registry,
            _tracker,
            NullLoggerFactory.Instance);
    }

    private async Task WaitForTerminalAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (_tracker.Current.State == IndexJobState.Running)
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Index job did not finish in time; last state was {_tracker.Current.State}.");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task HandleAsync_indexes_a_local_path_and_records_it_in_the_registry()
    {
        WriteFile("Sample.cs", """
            namespace Sample;

            public class Greeter
            {
                public string Greet(string name) => $"Hello, {name}!";
            }
            """);

        var handler = CreateHandler();

        var outcome = await handler.HandleAsync(new IndexRepositoryCommand(Path: _root));

        // Fire-and-forget: the outcome is Started with the default collection before the background
        // run has necessarily finished.
        Assert.Equal(IndexStartOutcome.Kind.Started, outcome.Status);
        Assert.Equal(CollectionNames.Default, outcome.Collection);
        Assert.Null(outcome.Error);

        await WaitForTerminalAsync();

        Assert.Equal(IndexJobState.Done, _tracker.Current.State);
        Assert.True(_tracker.Current.ChunksIndexed > 0);

        var entries = await _registry.ListAsync();
        var entry = Assert.Single(entries, e => e.Collection == CollectionNames.Default);
        Assert.Equal("local", entry.SourceType);
        Assert.Equal(_root, entry.Source);
        Assert.Equal(_tracker.Current.ChunksIndexed, entry.ChunkCount);
    }

    [Fact]
    public async Task HandleAsync_clones_via_the_git_service_and_records_a_remote_repo()
    {
        // The fake git service hands back _root (a real directory with a .cs file) as the checkout.
        WriteFile("Sample.cs", "namespace S; public class Sample { public void M() { } }");
        var git = new FakeGitRepoService(_root);
        var handler = CreateHandler(git);

        const string url = "https://github.com/owner/repo.git";
        var outcome = await handler.HandleAsync(new IndexRepositoryCommand(RepoUrl: url, Branch: "main"));

        Assert.Equal(IndexStartOutcome.Kind.Started, outcome.Status);
        var expectedCollection = RepoUrlInfo.Parse(url).Slug;
        Assert.Equal(expectedCollection, outcome.Collection);

        await WaitForTerminalAsync();

        Assert.Equal(IndexJobState.Done, _tracker.Current.State);
        Assert.Equal(url, git.RequestedUrl);
        Assert.Equal("main", git.RequestedBranch);

        var entries = await _registry.ListAsync();
        var entry = Assert.Single(entries, e => e.Collection == expectedCollection);
        Assert.Equal("github", entry.SourceType);
        Assert.Equal(url, entry.Source);
        Assert.Equal("main", entry.Branch);
    }

    [Fact]
    public async Task HandleAsync_returns_ValidationError_when_neither_path_nor_repoUrl_is_given()
    {
        var handler = CreateHandler();

        var outcome = await handler.HandleAsync(new IndexRepositoryCommand());

        Assert.Equal(IndexStartOutcome.Kind.ValidationError, outcome.Status);
        Assert.Contains("exactly one", outcome.Error);
    }

    [Fact]
    public async Task HandleAsync_returns_ValidationError_when_both_path_and_repoUrl_are_given()
    {
        var handler = CreateHandler();

        var outcome = await handler.HandleAsync(
            new IndexRepositoryCommand(Path: _root, RepoUrl: "https://github.com/owner/repo.git"));

        Assert.Equal(IndexStartOutcome.Kind.ValidationError, outcome.Status);
        Assert.Contains("exactly one", outcome.Error);
    }

    [Fact]
    public async Task HandleAsync_returns_ValidationError_for_a_missing_directory()
    {
        var handler = CreateHandler();

        var outcome = await handler.HandleAsync(
            new IndexRepositoryCommand(Path: Path.Combine(_root, "does-not-exist")));

        Assert.Equal(IndexStartOutcome.Kind.ValidationError, outcome.Status);
        Assert.Contains("Directory not found", outcome.Error);
    }

    [Fact]
    public async Task HandleAsync_returns_ValidationError_for_an_unparseable_repoUrl()
    {
        var handler = CreateHandler();

        var outcome = await handler.HandleAsync(new IndexRepositoryCommand(RepoUrl: "not-a-valid-url"));

        Assert.Equal(IndexStartOutcome.Kind.ValidationError, outcome.Status);
    }

    [Fact]
    public async Task HandleAsync_returns_AlreadyRunning_when_a_job_is_already_in_progress()
    {
        // Reserve the single slot up front so the handler's TryStart-guarded conflict path is hit.
        Assert.True(_tracker.TryStart("busy-collection", "busy"));
        var handler = CreateHandler();

        var outcome = await handler.HandleAsync(new IndexRepositoryCommand(Path: _root));

        Assert.Equal(IndexStartOutcome.Kind.AlreadyRunning, outcome.Status);
        Assert.Equal("An indexing job is already running.", outcome.Error);
    }
}
