using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Synth.Core.Embeddings;

namespace Synth.Api.Embeddings;

/// <summary>
/// DI wiring for Synth's embedding generator. Registers a
/// <see cref="ConfigurableEmbeddingGenerator"/> as the
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> (TInput = <c>string</c>,
/// TEmbedding = <c>Embedding&lt;float&gt;</c>): provider-selectable (Ollama or OpenAI) and
/// hot-swappable through <see cref="EmbeddingOptions"/>, with the Aspire-supplied Ollama
/// connection as the zero-config fallback.
/// </summary>
public static class EmbeddingServiceExtensions
{
    /// <summary>
    /// Aspire connection name for the Ollama embedding model. Must match the model
    /// resource registered in the AppHost (<c>ollama.AddModel("embeddings", ...)</c>),
    /// so the endpoint + model name arrive via service discovery — no hardcoded URL.
    /// </summary>
    public const string EmbeddingConnectionName = "embeddings";

    /// <summary>
    /// Registers <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> in DI as a
    /// <see cref="ConfigurableEmbeddingGenerator"/>: it binds <see cref="EmbeddingOptions"/> from the
    /// layered <c>Embedding</c> config section (same <c>Configure&lt;T&gt;(GetSection(...))</c> pattern as
    /// <c>AddSynthVcs</c>) and takes the Aspire Ollama connection as its fallback. With no Settings
    /// override this behaves exactly as before — Ollama via the Aspire endpoint — and the underlying
    /// client is created lazily, so this works without a live Ollama/OpenAI server or Docker.
    /// </summary>
    public static IHostApplicationBuilder AddSynthEmbeddings(this IHostApplicationBuilder builder)
    {
        builder.Services.Configure<EmbeddingOptions>(
            builder.Configuration.GetSection(EmbeddingOptions.SectionName));

        // Read the Aspire-supplied Ollama connection once, at registration time (as AddOllamaApiClient
        // did) — it's the fallback used until/unless Settings selects a different provider.
        var aspireDefault = OllamaConnection.Parse(
            builder.Configuration.GetConnectionString(EmbeddingConnectionName));

        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new ConfigurableEmbeddingGenerator(
                aspireDefault,
                sp.GetRequiredService<IOptionsMonitor<EmbeddingOptions>>()));

        return builder;
    }
}
