using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Synth.Application.Configuration;
using Synth.Application.Embeddings;
using Synth.Domain.Embeddings;

namespace Synth.Application.Tests.Embeddings;

// Proves SYNTH-69: the PUT /settings/embedding probe-before-persist body now lives in
// UpdateEmbeddingSettingsCommandHandler, behaving identically to the old EmbeddingSettingsEndpoints PUT
// handler. Runs offline against a recording IConfigSectionUpdater and a fake IEmbeddingGeneratorFactory
// (no live Ollama/OpenAI, no config store), so the probe gate and the partial-update mutation are
// asserted directly on the handler — the unit-level counterpart to EmbeddingSettingsControllerTests'
// HTTP round trip.
public class UpdateEmbeddingSettingsCommandHandlerTests
{
    [Fact]
    public async Task Non_object_body_is_a_validation_error_and_persists_nothing()
    {
        var updater = new RecordingUpdater();
        var handler = CreateHandler(updater);

        var result = await handler.HandleAsync(new UpdateEmbeddingSettingsCommand(Element("\"not an object\"")));

        Assert.False(result.Success);
        Assert.Equal("Request body must be a JSON object.", result.Error);
        Assert.False(updater.WasCalled);
    }

    [Fact]
    public async Task Valid_config_is_probed_then_persisted()
    {
        var factory = FakeEmbeddingGeneratorFactory.Succeeding();
        var updater = new RecordingUpdater();
        var handler = CreateHandler(updater, factory);

        var result = await handler.HandleAsync(new UpdateEmbeddingSettingsCommand(
            Body(new { provider = "OpenAI", openai = new { apiKey = "sk-live", model = "text-embedding-3-small" } })));

        Assert.True(result.Success);
        // The candidate config was probed for one embedding before anything was written...
        Assert.Equal(1, factory.ProbeCount);
        // ...and, the probe having succeeded, the values are genuinely written to the section.
        Assert.True(updater.WasCalled);
        Assert.Equal("OpenAI", updater.Section["Provider"]!.GetValue<string>());
        Assert.Equal("sk-live", updater.Section["OpenAI"]!["ApiKey"]!.GetValue<string>());
        Assert.Equal("text-embedding-3-small", updater.Section["OpenAI"]!["Model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Probe_failure_is_a_validation_error_and_persists_nothing()
    {
        var updater = new RecordingUpdater(Seed("""{ "Provider": "Ollama", "Ollama": { "Model": "nomic" } }"""));
        var handler = CreateHandler(updater, FakeEmbeddingGeneratorFactory.Failing());

        var result = await handler.HandleAsync(new UpdateEmbeddingSettingsCommand(
            Body(new { provider = "OpenAI", openai = new { apiKey = "sk-bad" } })));

        Assert.False(result.Success);
        Assert.Contains("probe failed", result.Error);
        // Nothing persisted: the section was never handed to the updater to mutate.
        Assert.False(updater.WasCalled);
        Assert.Equal("Ollama", updater.Section["Provider"]!.GetValue<string>());
    }

    [Fact]
    public async Task Probe_returning_an_empty_vector_is_rejected_without_persisting()
    {
        var updater = new RecordingUpdater();
        var handler = CreateHandler(updater, FakeEmbeddingGeneratorFactory.ReturningEmptyVector());

        var result = await handler.HandleAsync(new UpdateEmbeddingSettingsCommand(
            Body(new { provider = "Ollama", ollama = new { endpoint = "http://x:11434", model = "m" } })));

        Assert.False(result.Success);
        Assert.Contains("empty vector", result.Error);
        Assert.False(updater.WasCalled);
    }

    [Fact]
    public async Task Partial_update_of_only_the_model_leaves_the_stored_api_key_and_provider_alone()
    {
        var factory = FakeEmbeddingGeneratorFactory.Succeeding();
        var updater = new RecordingUpdater(
            Seed("""{ "Provider": "OpenAI", "OpenAI": { "ApiKey": "sk-keep", "Model": "old-model" } }"""));
        var handler = CreateHandler(updater, factory);

        var result = await handler.HandleAsync(new UpdateEmbeddingSettingsCommand(
            Body(new { openai = new { model = "new-model" } })));

        Assert.True(result.Success);
        // The model is updated; provider and key are not mentioned and must survive.
        Assert.Equal("new-model", updater.Section["OpenAI"]!["Model"]!.GetValue<string>());
        Assert.Equal("sk-keep", updater.Section["OpenAI"]!["ApiKey"]!.GetValue<string>());
        Assert.Equal("OpenAI", updater.Section["Provider"]!.GetValue<string>());
    }

    [Fact]
    public async Task Candidate_probe_sees_the_currently_stored_key_when_the_request_omits_it()
    {
        // The current options hold a key the caller does not resend; the probed candidate must merge it
        // in so a model-only update is validated against the real, key-bearing config.
        var factory = FakeEmbeddingGeneratorFactory.Succeeding();
        var options = new StaticOptionsMonitor<EmbeddingOptions>(new EmbeddingOptions
        {
            Provider = "OpenAI",
            OpenAI = { ApiKey = "sk-existing", Model = "old-model" },
        });
        var handler = CreateHandler(new RecordingUpdater(), factory, options);

        var result = await handler.HandleAsync(new UpdateEmbeddingSettingsCommand(
            Body(new { openai = new { model = "new-model" } })));

        Assert.True(result.Success);
        Assert.NotNull(factory.LastCandidate);
        Assert.Equal("sk-existing", factory.LastCandidate!.OpenAI.ApiKey); // merged from current options
        Assert.Equal("new-model", factory.LastCandidate.OpenAI.Model);     // overridden by the request
    }

    [Fact]
    public async Task Success_returns_the_masked_current_options_never_echoing_the_key()
    {
        // The masked response is built from IOptionsMonitor.CurrentValue (post-reload in production).
        var options = new StaticOptionsMonitor<EmbeddingOptions>(new EmbeddingOptions
        {
            Provider = "OpenAI",
            Ollama = { Endpoint = "http://ollama:11434", Model = "nomic" },
            OpenAI = { ApiKey = "sk-secret", Model = "text-embedding-3-small" },
        });
        var handler = CreateHandler(new RecordingUpdater(), FakeEmbeddingGeneratorFactory.Succeeding(), options);

        var result = await handler.HandleAsync(new UpdateEmbeddingSettingsCommand(
            Body(new { openai = new { model = "text-embedding-3-small" } })));

        Assert.True(result.Success);
        Assert.Equal("OpenAI", result.Response!.Provider);
        Assert.True(result.Response.OpenAI.ApiKeySet);
        Assert.Equal("text-embedding-3-small", result.Response.OpenAI.Model);
        Assert.Equal("http://ollama:11434", result.Response.Ollama.Endpoint);
        // The masked shape carries only a flag — the raw key value is never present.
        Assert.DoesNotContain("sk-secret", JsonSerializer.Serialize(result.Response));
    }

    private static UpdateEmbeddingSettingsCommandHandler CreateHandler(
        IConfigSectionUpdater updater,
        FakeEmbeddingGeneratorFactory? factory = null,
        IOptionsMonitor<EmbeddingOptions>? options = null) =>
        new(
            factory ?? FakeEmbeddingGeneratorFactory.Succeeding(),
            updater,
            options ?? new StaticOptionsMonitor<EmbeddingOptions>(new EmbeddingOptions()));

    private static JsonElement Body(object value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonObject Seed(string json) => JsonNode.Parse(json)!.AsObject();

    // Records the mutation the handler applies to the config section, seeded with any pre-existing
    // state, so the partial-update semantics can be asserted without a real IConfigStore.
    private sealed class RecordingUpdater(JsonObject? seed = null) : IConfigSectionUpdater
    {
        public bool WasCalled { get; private set; }
        public JsonObject Section { get; } = seed ?? new JsonObject();

        public Task UpdateSectionAsync(string sectionName, Action<JsonObject> mutate, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            mutate(Section);
            return Task.CompletedTask;
        }

        // Raw-document members (used only by the raw-settings handler) are not part of this flow.
        public Task<string> LoadDocumentAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReplaceDocumentAsync(string json, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    // A deterministic stand-in for IEmbeddingGeneratorFactory (mirrors the one in
    // EmbeddingSettingsControllerTests): builds a generator whose probe behavior (succeed / throw /
    // return an empty vector) is fixed per test, recording the candidate it was asked to build so the
    // merge logic can be asserted. No real provider is contacted.
    private sealed class FakeEmbeddingGeneratorFactory(Func<GeneratedEmbeddings<Embedding<float>>> probe)
        : IEmbeddingGeneratorFactory
    {
        public int ProbeCount { get; private set; }
        public EmbeddingOptions? LastCandidate { get; private set; }

        public static FakeEmbeddingGeneratorFactory Succeeding() =>
            new(() => new GeneratedEmbeddings<Embedding<float>>(
                [new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f })]));

        public static FakeEmbeddingGeneratorFactory Failing() =>
            new(() => throw new InvalidOperationException("provider unreachable"));

        public static FakeEmbeddingGeneratorFactory ReturningEmptyVector() =>
            new(() => new GeneratedEmbeddings<Embedding<float>>(
                [new Embedding<float>(ReadOnlyMemory<float>.Empty)]));

        public IEmbeddingGenerator<string, Embedding<float>> Create(EmbeddingOptions options)
        {
            LastCandidate = options;
            return new StubGenerator(() =>
            {
                ProbeCount++;
                return probe();
            });
        }

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
