using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Synth.Api.Indexing;
using Synth.Application;
using Synth.Application.Indexing;
using Synth.Domain.Vcs;

namespace Synth.Api.Tests;

// Proves POST /index drives IndexingPipeline end to end over HTTP. Since SYNTH-31 the work is
// detached: POST returns 202 immediately and the run is observed through GET /index/status rather
// than the response body. SYNTH-66 moved these routes from a Minimal API to IndexingController; the
// HTTP-level contract (routes, status codes) is unchanged, so these assertions hold as-is. The real
// Ollama-backed embedding generator is swapped for a deterministic fake so this runs without a live
// Ollama/Docker, mirroring the fake used in Synth.Application.Tests' IndexingPipelineTests.
public class IndexingControllerTests : IClassFixture<TestApiFactory>
{
    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private const int Dimensions = 8;

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var embeddings = new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(new float[Dimensions])));
            return Task.FromResult(embeddings);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    // The API serializes IndexJobState as its string name (JsonStringEnumConverter, configured in
    // Program.cs), so the test client must read it back the same way.
    private static readonly JsonSerializerOptions StatusJsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly WebApplicationFactory<Program> _factory;

    public IndexingControllerTests(TestApiFactory factory) =>
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator())));

    // Polls GET /index/status until the job leaves Running (Done or Failed), or times out. The
    // tracker is a process singleton so the background run's state is visible over HTTP.
    private static async Task<IndexJobStatus> WaitForTerminalStatusAsync(
        HttpClient client, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var status = await client.GetFromJsonAsync<IndexJobStatus>("/index/status", StatusJsonOptions);
            Assert.NotNull(status);
            if (status!.State is IndexJobState.Done or IndexJobState.Failed)
                return status;

            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Index job did not finish in time; last state was {status.State}.");

            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task Index_accepts_immediately_and_status_reaches_Done_with_counts()
    {
        var tempDir = Directory.CreateTempSubdirectory("synth-index-endpoint-test-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "Sample.cs"), """
                namespace Sample;

                public class Greeter
                {
                    public string Greet(string name) => $"Hello, {name}!";
                }
                """);

            var client = _factory.CreateClient();

            var response = await client.PostAsJsonAsync("/index", new { path = tempDir.FullName });

            // Fire-and-forget: the response is 202 and carries only {collection, status}, not a summary.
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            var status = await WaitForTerminalStatusAsync(client);
            Assert.Equal(IndexJobState.Done, status.State);
            Assert.Equal(1, status.FilesIndexed);
            Assert.True(status.ChunksIndexed > 0);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Index_returns_400_for_a_missing_directory()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/index", new { path = "/no/such/directory" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Index_returns_400_when_neither_path_nor_repoUrl_is_given()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/index", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Index_returns_400_for_an_unparseable_repoUrl_before_any_background_work()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/index", new { repoUrl = "not-a-valid-url" });

        // Repo-URL parsing is synchronous validation, so it still fails fast with 400 and never
        // starts a background job.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Index_indexes_a_remote_repo_by_url_and_records_it_in_the_registry()
    {
        using var fixture = GitRepoFixture.CreateWithCSharpFile();
        var workspace = Directory.CreateTempSubdirectory("synth-index-workspace-");
        try
        {
            // Point GitRepoService's workspace at a temp dir so the file:// clone lands there.
            var factory = _factory.WithWebHostBuilder(builder =>
                builder.UseSetting("Vcs:WorkspaceRoot", workspace.FullName));
            var client = factory.CreateClient();

            var response = await client.PostAsJsonAsync("/index", new { repoUrl = fixture.Url });

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            var status = await WaitForTerminalStatusAsync(client);
            Assert.Equal(IndexJobState.Done, status.State);
            Assert.True(status.ChunksIndexed > 0);

            var collection = RepoUrlInfo.Parse(fixture.Url).Slug;
            var repositories = await client.GetFromJsonAsync<List<RepositoryEntry>>("/repositories");
            Assert.NotNull(repositories);
            var entry = Assert.Single(repositories!, r => r.Collection == collection);
            Assert.Equal(fixture.Url, entry.Source);
            Assert.Equal(status.ChunksIndexed, entry.ChunkCount);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_second_Index_while_one_is_running_returns_409()
    {
        var tempDir = Directory.CreateTempSubdirectory("synth-index-conflict-test-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "Sample.cs"), """
                namespace Sample;

                public class Greeter
                {
                    public string Greet(string name) => $"Hello, {name}!";
                }
                """);

            var client = _factory.CreateClient();

            // Force the shared singleton tracker into Running deterministically (rather than racing two
            // real runs), so the endpoint's TryStart-guarded 409 path is exercised without flakiness.
            var tracker = _factory.Services.GetRequiredService<IIndexJobTracker>();
            Assert.True(tracker.TryStart("busy-collection", "busy"));
            try
            {
                var response = await client.PostAsJsonAsync("/index", new { path = tempDir.FullName });
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            }
            finally
            {
                // Return the tracker to a terminal state so sibling tests sharing this singleton can
                // start their own runs again.
                tracker.Complete(filesIndexed: 0, filesSkipped: 0, chunksIndexed: 0);
            }
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
