using Microsoft.Extensions.AI;
using Synth.Application.Embeddings;
using Synth.Domain.Embeddings;

namespace Synth.Infrastructure.Embeddings;

/// <summary>
/// Production <see cref="IEmbeddingGeneratorFactory"/>: defers to
/// <see cref="ConfigurableEmbeddingGenerator.BuildGenerator"/> so the probe path constructs providers
/// with exactly the same logic as the live generator, against the same Aspire Ollama fallback. The
/// <see cref="IEmbeddingGeneratorFactory"/> port itself lives in <c>Synth.Application</c> (SYNTH-69) so
/// the settings command handler can depend on it without Application referencing Infrastructure.
/// </summary>
public sealed class EmbeddingGeneratorFactory(OllamaConnection aspireDefault) : IEmbeddingGeneratorFactory
{
    public IEmbeddingGenerator<string, Embedding<float>> Create(EmbeddingOptions options) =>
        ConfigurableEmbeddingGenerator.BuildGenerator(options, aspireDefault);
}
