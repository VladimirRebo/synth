using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synth.Api.Configuration;
using Synth.Api.Embeddings;
using Synth.Domain.Embeddings;
using Synth.Domain.Configuration;

namespace Synth.Api.Tests;

// Drives GET/PUT /settings/embedding over HTTP. As with the VCS settings tests, one in-memory
// IConfigStore backs both the endpoint's writes and a configuration layer, so the round trip is
// hermetic and the store's Changed event genuinely reloads IOptionsMonitor<EmbeddingOptions>. The
// network-talking piece (the probe generator) is faked, so no live Ollama/OpenAI is needed and the
// probe can be made to succeed or fail deterministically.
public class EmbeddingSettingsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public EmbeddingSettingsEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

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
