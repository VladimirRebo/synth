using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Synth.Api.Mcp;
using Synth.Application.Cqrs;
using Synth.Application.Indexing;
using Synth.Domain;

namespace Synth.Api.Tests;

// Proves SYNTH-36: the `index_code` MCP tool triggers indexing through the same shared flow
// (IndexRepositoryCommandHandler) that POST /index uses, with the same validation/conflict rules,
// and — like the REST endpoint — returns immediately (fire-and-forget) rather than blocking until
// indexing finishes. Runs offline: the real Ollama-backed embedding generator is swapped for a
// deterministic fake (mirroring IndexingEndpointTests), so no live Ollama/Qdrant/git is needed.
public class IndexCodeMcpToolTests : IClassFixture<WebApplicationFactory<Program>>
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

    private readonly WebApplicationFactory<Program> _factory;

    public IndexCodeMcpToolTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator())));

    // Invokes the tool with the real DI-resolved command handler, exactly as the MCP runtime would.
    // The handler's HandleAsync completes synchronously (validation + fire-and-forget dispatch), so
    // blocking on the returned task here is safe and keeps the sync tests below unchanged.
    private IndexCodeResult Invoke(string? path = null, string? repoUrl = null, string? branch = null)
    {
        var handler = _factory.Services
            .GetRequiredService<ICommandHandler<IndexRepositoryCommand, IndexStartOutcome>>();
        return IndexCodeTool.IndexCode(handler, path, repoUrl, branch).GetAwaiter().GetResult();
    }

    private async Task WaitForTerminalAsync(TimeSpan? timeout = null)
    {
        var tracker = _factory.Services.GetRequiredService<IIndexJobTracker>();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (tracker.Current.State == IndexJobState.Running)
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Index job did not finish in time.");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task Index_code_starts_a_job_and_returns_the_collection_immediately()
    {
        var tempDir = Directory.CreateTempSubdirectory("synth-index-code-tool-test-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "Sample.cs"), """
                namespace Sample;

                public class Greeter
                {
                    public string Greet(string name) => $"Hello, {name}!";
                }
                """);

            var result = Invoke(path: tempDir.FullName);

            // Fire-and-forget: the tool returns "started" with the resolved collection before the
            // background indexing has necessarily finished.
            Assert.Equal("started", result.Status);
            Assert.Equal(CollectionNames.Default, result.Collection);
            Assert.Null(result.Error);

            // Let the detached run finish so it doesn't leak into sibling tests sharing the tracker.
            await WaitForTerminalAsync();
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Index_code_rejects_when_neither_path_nor_repoUrl_is_given()
    {
        var result = Invoke();

        Assert.Equal("rejected", result.Status);
        Assert.Null(result.Collection);
        Assert.Contains("exactly one", result.Error);
    }

    [Fact]
    public void Index_code_rejects_when_both_path_and_repoUrl_are_given()
    {
        var result = Invoke(path: "/some/path", repoUrl: "https://github.com/owner/repo.git");

        Assert.Equal("rejected", result.Status);
        Assert.Contains("exactly one", result.Error);
    }

    [Fact]
    public void Index_code_rejects_when_a_job_is_already_running()
    {
        var tempDir = Directory.CreateTempSubdirectory("synth-index-code-conflict-test-");
        try
        {
            // Force the shared singleton tracker into Running deterministically so the TryStart-guarded
            // conflict path is exercised without racing two real runs.
            var tracker = _factory.Services.GetRequiredService<IIndexJobTracker>();
            Assert.True(tracker.TryStart("busy-collection", "busy"));
            try
            {
                var result = Invoke(path: tempDir.FullName);

                Assert.Equal("rejected", result.Status);
                Assert.Equal("An indexing job is already running.", result.Error);
            }
            finally
            {
                tracker.Complete(filesIndexed: 0, filesSkipped: 0, chunksIndexed: 0);
            }
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Index_code_tool_is_registered_on_the_mcp_server()
    {
        // The tool being resolvable as an McpServerTool proves it was wired into
        // AddMcpServer().WithTools<IndexCodeTool>() over the HTTP transport.
        var tools = _factory.Services.GetServices<McpServerTool>();

        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "index_code");
    }
}
