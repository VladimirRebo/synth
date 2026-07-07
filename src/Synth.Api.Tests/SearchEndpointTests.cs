using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Synth.Api.Mcp;

namespace Synth.Api.Tests;

// Proves GET /search actually drives CodeSearchService end to end over HTTP: index a real
// directory first (via POST /index), then search it. The real Ollama-backed embedding
// generator is swapped for a deterministic fake, mirroring IndexingEndpointTests, so this
// runs without a live Ollama/Docker.
public class SearchEndpointTests : IClassFixture<WebApplicationFactory<Program>>
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

    public SearchEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator())));

    [Fact]
    public async Task Search_finds_a_chunk_from_a_previously_indexed_directory()
    {
        var tempDir = Directory.CreateTempSubdirectory("synth-search-endpoint-test-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "Greeter.cs"), """
                namespace Sample;

                public class Greeter
                {
                    public string Greet(string name) => $"Hello, {name}!";
                }
                """);

            var client = _factory.CreateClient();
            var indexResponse = await client.PostAsJsonAsync("/index", new { path = tempDir.FullName });
            Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);

            var searchResponse = await client.GetAsync("/search?q=greet");

            Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
            var raw = await searchResponse.Content.ReadAsStringAsync();
            // ChunkType must serialize as its string name (e.g. "Method"), not the underlying
            // int — the JS client types it as a string, and it must match the MCP tool's shape.
            Assert.DoesNotContain("\"chunkType\":0", raw);
            Assert.DoesNotMatch("\"chunkType\":\\d", raw);

            var deserializeOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            deserializeOptions.Converters.Add(new JsonStringEnumConverter());
            var results = JsonSerializer.Deserialize<List<CodeSearchResult>>(raw, deserializeOptions);
            Assert.NotNull(results);
            Assert.Contains(results, r => r.ClassName == "Greeter");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Search_returns_400_when_q_is_missing()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/search");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
