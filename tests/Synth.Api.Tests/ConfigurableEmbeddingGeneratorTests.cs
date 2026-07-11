using Microsoft.Extensions.AI;
using OllamaSharp;
using Synth.Api.Embeddings;
using Synth.Domain.Embeddings;

namespace Synth.Api.Tests;

// SYNTH-21: ConfigurableEmbeddingGenerator is provider-selectable (Ollama/OpenAI) and hot-swappable
// via IOptionsMonitor<EmbeddingOptions>, with the Aspire Ollama connection as the zero-config fallback.
// No live Ollama/OpenAI is needed — construction/resolution opens no socket, and the tests never call
// an actual provider. Config changes are pushed through a mutable in-memory IOptionsMonitor.
public class ConfigurableEmbeddingGeneratorTests
{
    // Stands in for the "Endpoint;Model" connection string Aspire injects for the embeddings resource.
    private static readonly OllamaConnection AspireDefault =
        new("http://localhost:11434", "nomic-embed-text");

    private static ConfigurableEmbeddingGenerator New(MutableOptionsMonitor<EmbeddingOptions> monitor) =>
        new(AspireDefault, monitor);

    // GetService(typeof(OllamaApiClient)) returns the inner OllamaApiClient when the current provider is
    // Ollama, so tests can observe which endpoint/model is active and whether the instance was rebuilt.
    private static OllamaApiClient InnerOllama(ConfigurableEmbeddingGenerator generator) =>
        Assert.IsType<OllamaApiClient>(generator.GetService(typeof(OllamaApiClient)));

    [Fact]
    public void Default_config_uses_the_Aspire_Ollama_connection_unchanged()
    {
        var monitor = new MutableOptionsMonitor<EmbeddingOptions>(new EmbeddingOptions());
        using var generator = New(monitor);

        var inner = InnerOllama(generator);

        Assert.Equal("localhost", inner.Uri.Host);
        Assert.Equal(11434, inner.Uri.Port);
        Assert.Equal("nomic-embed-text", inner.SelectedModel);
    }

    [Fact]
    public void Ollama_overrides_replace_only_the_supplied_fields()
    {
        var monitor = new MutableOptionsMonitor<EmbeddingOptions>(new EmbeddingOptions
        {
            Provider = "Ollama",
            Ollama = { Endpoint = "http://ollama.internal:9999" }, // Model left unset → falls back
        });
        using var generator = New(monitor);

        var inner = InnerOllama(generator);

        Assert.Equal("ollama.internal", inner.Uri.Host);
        Assert.Equal(9999, inner.Uri.Port);
        Assert.Equal("nomic-embed-text", inner.SelectedModel); // fell back to the Aspire model
    }

    [Fact]
    public void Config_change_is_picked_up_without_restart()
    {
        var monitor = new MutableOptionsMonitor<EmbeddingOptions>(new EmbeddingOptions());
        using var generator = New(monitor);

        var before = InnerOllama(generator);
        Assert.Equal("localhost", before.Uri.Host);

        // Save new Settings at runtime — no rebuild of the host.
        monitor.Set(new EmbeddingOptions
        {
            Provider = "Ollama",
            Ollama = { Endpoint = "http://ollama.internal:9999", Model = "mxbai-embed-large" },
        });

        var after = InnerOllama(generator);

        Assert.NotSame(before, after); // the inner generator was rebuilt
        Assert.Equal("ollama.internal", after.Uri.Host);
        Assert.Equal("mxbai-embed-large", after.SelectedModel);
    }

    [Fact]
    public void Unchanged_config_reuses_the_same_inner_generator()
    {
        var monitor = new MutableOptionsMonitor<EmbeddingOptions>(new EmbeddingOptions());
        using var generator = New(monitor);

        var first = InnerOllama(generator);
        var second = InnerOllama(generator);

        Assert.Same(first, second); // no rebuild when nothing changed
    }

    [Fact]
    public async Task Incomplete_OpenAI_config_does_not_throw_until_used()
    {
        var monitor = new MutableOptionsMonitor<EmbeddingOptions>(new EmbeddingOptions
        {
            Provider = "OpenAI", // no ApiKey configured
        });

        // Construction and DI-style resolution/probing must not throw on the invalid config.
        using var generator = New(monitor);
        Assert.Null(generator.GetService(typeof(OllamaApiClient)));

        // The misconfiguration only surfaces when an embedding is actually requested.
        await Assert.ThrowsAsync<InvalidOperationException>(() => generator.GenerateAsync(["hello"]));
    }
}
