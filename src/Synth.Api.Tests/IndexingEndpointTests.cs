using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Synth.Core;

namespace Synth.Api.Tests;

// Proves POST /index actually drives IndexingPipeline end to end over HTTP. The real
// Ollama-backed embedding generator is swapped for a deterministic fake so this runs
// without a live Ollama/Docker, mirroring the fake used in Synth.Core.Tests'
// IndexingPipelineTests.
public class IndexingEndpointTests : IClassFixture<WebApplicationFactory<Program>>
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

    private readonly WebApplicationFactory<Program> _factory;

    public IndexingEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator())));

    [Fact]
    public async Task Index_indexes_a_real_directory_and_returns_a_summary()
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

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var summary = await response.Content.ReadFromJsonAsync<IndexingSummary>();
            Assert.Equal(1, summary.FilesIndexed);
            Assert.True(summary.ChunksIndexed > 0);
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
}
