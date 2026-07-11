using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Synth.Api.Mcp;
using Synth.Api.Search;

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

    // Polls GET /index/status until the background job reports Done (state serializes as its string
    // name), so a search only runs once the index is actually populated.
    private static async Task WaitForIndexDoneAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var status = await client.GetStringAsync("/index/status");
            if (status.Contains("\"Done\"") || status.Contains("\"Failed\""))
                return;

            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Index job did not finish in time; last status was {status}.");

            await Task.Delay(25);
        }
    }

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
            // POST /index is fire-and-forget since SYNTH-31: it returns 202 and indexes in the
            // background, so wait for the job to finish (via GET /index/status) before searching.
            var indexResponse = await client.PostAsJsonAsync("/index", new { path = tempDir.FullName });
            Assert.Equal(HttpStatusCode.Accepted, indexResponse.StatusCode);
            await WaitForIndexDoneAsync(client);

            var searchResponse = await client.GetAsync("/search?q=greet");

            Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
            var raw = await searchResponse.Content.ReadAsStringAsync();
            // ChunkType must serialize as its string name (e.g. "Method"), not the underlying
            // int — the JS client types it as a string, and it must match the MCP tool's shape.
            Assert.DoesNotContain("\"chunkType\":0", raw);
            Assert.DoesNotMatch("\"chunkType\":\\d", raw);

            // score must be present as a plain JSON number (the fake embedding generator
            // produces an all-zero vector, so cosine similarity — and thus the final rerank
            // score — is deterministically 0 here; a real Ollama vector would vary).
            Assert.Matches("\"score\":\\s*-?\\d", raw);

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

    // Browse endpoint (SYNTH-47): GET /repositories/{collection}/files/{*relativePath} returns
    // every chunk stored for a file, in ascending line order, each carrying its assembled
    // EmbeddingText. Indexes a real multi-method file first (via POST /index) so several chunks
    // exist for one path, then browses it — same fake-embedding, no-Ollama setup as the search test.
    [Fact]
    public async Task Browse_returns_a_files_chunks_in_line_order_with_embedding_text()
    {
        var tempDir = Directory.CreateTempSubdirectory("synth-browse-endpoint-test-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir.FullName, "Calculator.cs"), """
                namespace Sample;

                public class Calculator
                {
                    public int Add(int a, int b) => a + b;

                    public int Subtract(int a, int b) => a - b;
                }
                """);

            var client = _factory.CreateClient();
            var indexResponse = await client.PostAsJsonAsync("/index", new { path = tempDir.FullName });
            Assert.Equal(HttpStatusCode.Accepted, indexResponse.StatusCode);
            await WaitForIndexDoneAsync(client);

            // Local-path indexing uses the default collection, so browse it back by relative path.
            var response = await client.GetAsync("/repositories/default/files/Calculator.cs");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var raw = await response.Content.ReadAsStringAsync();
            // chunkType must serialize as its string name (e.g. "Method"), matching the client type.
            Assert.DoesNotMatch("\"chunkType\":\\d", raw);

            var deserializeOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            deserializeOptions.Converters.Add(new JsonStringEnumConverter());
            var chunks = JsonSerializer.Deserialize<List<FileChunkResult>>(raw, deserializeOptions);
            Assert.NotNull(chunks);
            Assert.NotEmpty(chunks);

            // Chunks come back in ascending StartLine order (GetByFileAsync's contract).
            var startLines = chunks.Select(c => c.StartLine).ToList();
            Assert.Equal(startLines.OrderBy(line => line).ToList(), startLines);

            // The whole point of the endpoint: each chunk carries its assembled embedding text,
            // prefixed with [code] for source (not [docs]) — not just the raw content.
            Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c.EmbeddingText)));
            Assert.Contains(chunks, c => c.EmbeddingText.Contains("[code]"));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Browse_returns_404_when_the_file_has_no_chunks()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/repositories/default/files/NeverIndexed.cs");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
