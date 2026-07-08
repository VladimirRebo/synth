using Microsoft.Extensions.AI;
using Synth.Core.Embeddings;

namespace Synth.Api.Embeddings;

/// <summary>
/// Builds a throwaway <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> from a candidate
/// <see cref="EmbeddingOptions"/> snapshot. SYNTH-22's <c>PUT /settings/embedding</c> uses this to
/// probe a config (generate one real embedding) <b>before</b> persisting it, so a broken provider is
/// never saved. It's a seam, not just a helper: tests replace it with a fake so the probe can be made
/// to succeed or fail deterministically without a live Ollama/OpenAI server.
/// </summary>
public interface IEmbeddingGeneratorFactory
{
    /// <summary>Builds the generator selected by <paramref name="options"/> (never connects on construction).</summary>
    IEmbeddingGenerator<string, Embedding<float>> Create(EmbeddingOptions options);
}

/// <summary>
/// Production <see cref="IEmbeddingGeneratorFactory"/>: defers to
/// <see cref="ConfigurableEmbeddingGenerator.BuildGenerator"/> so the probe path constructs providers
/// with exactly the same logic as the live generator, against the same Aspire Ollama fallback.
/// </summary>
public sealed class EmbeddingGeneratorFactory(OllamaConnection aspireDefault) : IEmbeddingGeneratorFactory
{
    public IEmbeddingGenerator<string, Embedding<float>> Create(EmbeddingOptions options) =>
        ConfigurableEmbeddingGenerator.BuildGenerator(options, aspireDefault);
}
