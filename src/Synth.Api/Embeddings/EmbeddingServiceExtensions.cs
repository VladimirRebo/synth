using Microsoft.Extensions.Hosting;

namespace Synth.Api.Embeddings;

/// <summary>
/// DI wiring for Synth's embedding generator. Registers an
/// <see cref="Microsoft.Extensions.AI.IEmbeddingGenerator{TInput,TEmbedding}"/>
/// (TInput = <c>string</c>, TEmbedding = <c>Embedding&lt;float&gt;</c>) backed by
/// Ollama via the Aspire Community Toolkit client integration.
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
    /// Registers <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> in DI,
    /// backed by the Ollama resource referenced from the AppHost. The underlying Ollama
    /// client is created lazily and opens no socket at registration/resolution time, so
    /// this works without a live Ollama server or Docker (mirrors the Mongo pattern).
    /// </summary>
    public static IHostApplicationBuilder AddSynthEmbeddings(this IHostApplicationBuilder builder)
    {
        builder.AddOllamaApiClient(EmbeddingConnectionName)
            .AddEmbeddingGenerator();

        return builder;
    }
}
