using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synth.Application.Embeddings;
using Synth.Infrastructure.Configuration;
using Synth.Domain.Embeddings;
using Synth.Domain.Configuration;

namespace Synth.Api.Tests;

// Drives GET/PUT /settings/embedding over HTTP against EmbeddingSettingsController (SYNTH-69). As with
// the VCS settings tests, one in-memory IConfigStore backs both the controller's writes and a
// configuration layer, so the round trip is hermetic and the store's Changed event genuinely reloads
// IOptionsMonitor<EmbeddingOptions>. The network-talking piece (the probe generator) is faked, so no
// live Ollama/OpenAI is needed and the probe can be made to succeed or fail deterministically.
public class EmbeddingSettingsControllerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public EmbeddingSettingsControllerTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private (HttpClient Client, InMemoryConfigStore Store) CreateClient(
        string? initialJson = null, IEmbeddingGeneratorFactory? probeFactory = null)
    {
        var store = new InMemoryConfigStore(initialJson);
        probeFactory ??= FakeEmbeddingGeneratorFactory.Succeeding();
        var client = _factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                    config.Add(new ConfigStoreConfigurationSource(store)));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IConfigStore>();
                    services.AddSingleton<IConfigStore>(store);
                    // Swap the real probe factory for a deterministic fake.
                    services.RemoveAll<IEmbeddingGeneratorFactory>();
                    services.AddSingleton(probeFactory);
                });
            })
            .CreateClient();
        return (client, store);
    }

    [Fact]
    public async Task Get_masks_the_api_key_and_never_echoes_it()
    {
        var (client, _) = CreateClient(
            """{ "Embedding": { "Provider": "OpenAI", "OpenAI": { "ApiKey": "sk-topsecret", "Model": "text-embedding-3-small" } } }""");

        var response = await client.GetAsync("/settings/embedding");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("sk-topsecret", payload);
        using var json = JsonDocument.Parse(payload);
        Assert.Equal("OpenAI", json.RootElement.GetProperty("provider").GetString());
        Assert.True(json.RootElement.GetProperty("openai").GetProperty("apiKeySet").GetBoolean());
        Assert.Equal("text-embedding-3-small", json.RootElement.GetProperty("openai").GetProperty("model").GetString());
    }

    [Fact]
    public async Task Successful_put_persists_and_is_reflected_masked_in_a_subsequent_get()
    {
        var (client, store) = CreateClient(probeFactory: FakeEmbeddingGeneratorFactory.Succeeding());

        var put = await client.PutAsJsonAsync("/settings/embedding", new
        {
            provider = "OpenAI",
            openai = new { apiKey = "sk-live", model = "text-embedding-3-small" },
        });
        put.EnsureSuccessStatusCode();
        Assert.DoesNotContain("sk-live", await put.Content.ReadAsStringAsync());

        var response = await client.GetAsync("/settings/embedding");
        var getBody = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sk-live", getBody); // masking never leaks the raw key
        var get = JsonDocument.Parse(getBody).RootElement;
        Assert.Equal("OpenAI", get.GetProperty("provider").GetString());
        Assert.True(get.GetProperty("openai").GetProperty("apiKeySet").GetBoolean());
        Assert.Equal("text-embedding-3-small", get.GetProperty("openai").GetProperty("model").GetString());

        // The key is genuinely stored (just never echoed).
        using var stored = JsonDocument.Parse(store.Current!);
        Assert.Equal("sk-live",
            stored.RootElement.GetProperty("Embedding").GetProperty("OpenAI").GetProperty("ApiKey").GetString());
    }

    [Fact]
    public async Task Probe_failure_returns_400_and_leaves_the_config_unchanged()
    {
        var (client, store) = CreateClient(
            """{ "Embedding": { "Provider": "Ollama", "Ollama": { "Endpoint": "http://good:11434", "Model": "nomic" } } }""",
            FakeEmbeddingGeneratorFactory.Failing());
        var before = store.Current;

        var put = await client.PutAsJsonAsync("/settings/embedding", new
        {
            provider = "OpenAI",
            openai = new { apiKey = "sk-bad" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);

        // Nothing was persisted: the stored document is byte-for-byte what it was before the failed PUT.
        Assert.Equal(before, store.Current);
        Assert.DoesNotContain("sk-bad", store.Current!);

        // ...and a subsequent GET still shows the original config, untouched.
        var get = await client.GetFromJsonAsync<JsonElement>("/settings/embedding");
        Assert.Equal("Ollama", get.GetProperty("provider").GetString());
        Assert.False(get.GetProperty("openai").GetProperty("apiKeySet").GetBoolean());
        Assert.Equal("http://good:11434", get.GetProperty("ollama").GetProperty("endpoint").GetString());
    }

    [Fact]
    public async Task Probe_returning_an_empty_vector_is_rejected_without_persisting()
    {
        var (client, store) = CreateClient(probeFactory: FakeEmbeddingGeneratorFactory.ReturningEmptyVector());

        var put = await client.PutAsJsonAsync("/settings/embedding", new
        {
            provider = "Ollama",
            ollama = new { endpoint = "http://x:11434", model = "m" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Null(store.Current); // never saved
    }

    [Fact]
    public async Task Partial_put_of_only_the_model_leaves_the_stored_api_key_and_provider_alone()
    {
        var (client, store) = CreateClient(
            """{ "Embedding": { "Provider": "OpenAI", "OpenAI": { "ApiKey": "sk-keep", "Model": "old-model" } } }""",
            FakeEmbeddingGeneratorFactory.Succeeding());

        // Update only the model; provider and key are not mentioned and must survive.
        var put = await client.PutAsJsonAsync("/settings/embedding", new { openai = new { model = "new-model" } });
        put.EnsureSuccessStatusCode();

        var get = await client.GetFromJsonAsync<JsonElement>("/settings/embedding");
        Assert.Equal("OpenAI", get.GetProperty("provider").GetString());
        Assert.True(get.GetProperty("openai").GetProperty("apiKeySet").GetBoolean());
        Assert.Equal("new-model", get.GetProperty("openai").GetProperty("model").GetString());

        // The stored key is genuinely still present (the partial write didn't drop it).
        using var stored = JsonDocument.Parse(store.Current!);
        Assert.Equal("sk-keep",
            stored.RootElement.GetProperty("Embedding").GetProperty("OpenAI").GetProperty("ApiKey").GetString());
    }

    // --- Ollama model-picker routes (SYNTH-70, merged in from the former OllamaModelEndpoints) ---
    // These drive GET .../ollama/models, POST .../ollama/pull, GET .../ollama/pull/status over HTTP against
    // the same controller. The real Ollama HTTP call is faked via a stand-in IHttpClientFactory, so no live
    // Ollama is contacted, and an in-memory IConfigStore supplies Embedding:Ollama:Endpoint so the endpoint
    // resolves without an Aspire connection. Deeper pull-orchestration/progress-parsing coverage lives in
    // PullOllamaModelCommandHandlerTests; here we assert the controller's status-code mapping and wiring.

    private const string OllamaEndpoint = "http://ollama.test:11434";

    private HttpClient CreateOllamaClient(FakeHttpClientFactory httpFactory, IOllamaPullTracker? tracker = null)
    {
        var store = new InMemoryConfigStore(
            $$"""{ "Embedding": { "Ollama": { "Endpoint": "{{OllamaEndpoint}}" } } }""");
        return _factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                    config.Add(new ConfigStoreConfigurationSource(store)));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IConfigStore>();
                    services.AddSingleton<IConfigStore>(store);
                    // Swap the real IHttpClientFactory so no live Ollama is hit.
                    services.RemoveAll<IHttpClientFactory>();
                    services.AddSingleton<IHttpClientFactory>(httpFactory);
                    if (tracker is not null)
                    {
                        services.RemoveAll<IOllamaPullTracker>();
                        services.AddSingleton(tracker);
                    }
                });
            })
            .CreateClient();
    }

    [Fact]
    public async Task Ollama_models_proxies_tags_and_returns_the_model_names()
    {
        var httpFactory = FakeHttpClientFactory.Responding((request, _) =>
        {
            Assert.Equal($"{OllamaEndpoint}/api/tags", request.RequestUri?.ToString());
            return Json("""
                { "models": [ { "name": "nomic-embed-text:latest" }, { "name": "llama3:8b" } ] }
                """);
        });

        var client = CreateOllamaClient(httpFactory);

        var models = await client.GetFromJsonAsync<List<string>>("/settings/embedding/ollama/models");

        Assert.Equal(["nomic-embed-text:latest", "llama3:8b"], models);
    }

    [Fact]
    public async Task Ollama_models_returns_502_when_ollama_is_unreachable()
    {
        var httpFactory = FakeHttpClientFactory.Throwing();

        var client = CreateOllamaClient(httpFactory);

        var response = await client.GetAsync("/settings/embedding/ollama/models");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Ollama_pull_accepts_immediately_and_status_reaches_Done()
    {
        // A canned /api/pull stream: manifest -> a download line with byte counts -> success. This exercises
        // the whole controller→command→tracker wiring end-to-end over HTTP.
        var httpFactory = FakeHttpClientFactory.Responding((request, _) =>
        {
            Assert.Equal($"{OllamaEndpoint}/api/pull", request.RequestUri?.ToString());
            return Ndjson(
                """{ "status": "pulling manifest" }""",
                """{ "status": "downloading", "completed": 50, "total": 100 }""",
                """{ "status": "success" }""");
        });

        var client = CreateOllamaClient(httpFactory);

        var response = await client.PostAsJsonAsync(
            "/settings/embedding/ollama/pull", new { model = "nomic-embed-text" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var status = await WaitForTerminalStatusAsync(client);
        Assert.Equal("Done", status.GetProperty("state").GetString());
        Assert.Equal("nomic-embed-text", status.GetProperty("model").GetString());
        Assert.Null(status.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Ollama_pull_returns_409_when_one_is_already_running()
    {
        // A tracker pre-reserved as Running: the controller must reject a concurrent pull without dispatch.
        var tracker = new InMemoryOllamaPullTracker();
        tracker.TryStart("already-running");
        var httpFactory = FakeHttpClientFactory.Responding((_, _) => Ndjson("""{ "status": "success" }"""));

        var client = CreateOllamaClient(httpFactory, tracker);

        var response = await client.PostAsJsonAsync(
            "/settings/embedding/ollama/pull", new { model = "another" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Ollama_pull_rejects_a_blank_model_with_400()
    {
        var httpFactory = FakeHttpClientFactory.Responding((_, _) => Ndjson("""{ "status": "success" }"""));

        var client = CreateOllamaClient(httpFactory);

        var response = await client.PostAsJsonAsync("/settings/embedding/ollama/pull", new { model = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Polls GET .../pull/status until the pull leaves Running (Done or Failed), or times out. The tracker
    // is a process singleton, so the background pull's state is visible over HTTP.
    private static async Task<JsonElement> WaitForTerminalStatusAsync(HttpClient client, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var status = await client.GetFromJsonAsync<JsonElement>("/settings/embedding/ollama/pull/status");
            var state = status.GetProperty("state").GetString();
            if (state is "Done" or "Failed")
                return status;

            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Pull did not finish in time; last state was {state}.");

            await Task.Delay(25);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Ndjson(params string[] lines) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                string.Join('\n', lines.Select(JsonMinify)), Encoding.UTF8, "application/x-ndjson"),
        };

    // Collapse the multi-line raw string literals above into single physical lines so the pull's
    // line-by-line reader sees exactly one JSON object per line.
    private static string JsonMinify(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement);
    }

    // A deterministic stand-in for IHttpClientFactory: every CreateClient() returns an HttpClient over a
    // stub handler that either invokes a supplied responder or throws (network failure). No live Ollama.
    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly StubHandler _handler;

        private FakeHttpClientFactory(StubHandler handler) => _handler = handler;

        public static FakeHttpClientFactory Responding(
            Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) =>
            new(new StubHandler(responder));

        public static FakeHttpClientFactory Throwing() =>
            new(new StubHandler((_, _) => throw new HttpRequestException("simulated network failure")));

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);

        private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(responder(request, cancellationToken));
        }
    }

    // A deterministic stand-in for IEmbeddingGeneratorFactory: builds a generator whose probe behavior
    // (succeed / throw / return an empty vector) is fixed per test, so no real provider is contacted.
    private sealed class FakeEmbeddingGeneratorFactory(
        Func<GeneratedEmbeddings<Embedding<float>>> probe) : IEmbeddingGeneratorFactory
    {
        public static FakeEmbeddingGeneratorFactory Succeeding() =>
            new(() => new GeneratedEmbeddings<Embedding<float>>(
                [new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f })]));

        public static FakeEmbeddingGeneratorFactory Failing() =>
            new(() => throw new InvalidOperationException("provider unreachable"));

        public static FakeEmbeddingGeneratorFactory ReturningEmptyVector() =>
            new(() => new GeneratedEmbeddings<Embedding<float>>(
                [new Embedding<float>(ReadOnlyMemory<float>.Empty)]));

        public IEmbeddingGenerator<string, Embedding<float>> Create(EmbeddingOptions options) =>
            new StubGenerator(probe);

        private sealed class StubGenerator(Func<GeneratedEmbeddings<Embedding<float>>> probe)
            : IEmbeddingGenerator<string, Embedding<float>>
        {
            public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
                IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(probe());

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}
