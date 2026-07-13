using Microsoft.Extensions.AI;
using Synth.Domain.Embeddings;

namespace Synth.Application.Embeddings;

/// <summary>
/// Builds a throwaway <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> from a candidate
/// <see cref="EmbeddingOptions"/> snapshot. SYNTH-22's <c>PUT /settings/embedding</c> uses this to
/// probe a config (generate one real embedding) <b>before</b> persisting it, so a broken provider is
/// never saved. It's a seam, not just a helper: tests replace it with a fake so the probe can be made
/// to succeed or fail deterministically without a live Ollama/OpenAI server.
/// <para>
/// This is the Application-layer port (SYNTH-69, issue #82): <see cref="UpdateEmbeddingSettingsCommandHandler"/>
/// depends on it so Application never references Infrastructure — the same seam
/// <see cref="Configuration.IConfigSectionUpdater"/> provides over <c>ConfigSectionUpdater</c>. The
/// concrete <c>EmbeddingGeneratorFactory</c> lives in Infrastructure alongside the generator it defers to.
/// </para>
/// </summary>
public interface IEmbeddingGeneratorFactory
{
    /// <summary>Builds the generator selected by <paramref name="options"/> (never connects on construction).</summary>
    IEmbeddingGenerator<string, Embedding<float>> Create(EmbeddingOptions options);
}
