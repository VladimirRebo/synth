namespace Synth.Core.Embeddings;

/// <summary>
/// Configuration for the embedding generator, bound from the <c>Embedding</c> config section through
/// the layered <c>IConfigStore</c>/<c>IOptionsMonitor</c> machinery (same pattern as <see cref="Vcs.VcsOptions"/>).
/// Consumed as <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/> so a provider/model
/// change saved through Settings is picked up without restarting the Aspire host.
/// <para>
/// <b>Zero-config default:</b> when <see cref="Provider"/> is null/empty nothing here overrides anything —
/// embeddings keep using Ollama via the Aspire-supplied connection string, exactly as before. The override
/// only takes effect once a provider has actually been selected.
/// </para>
/// </summary>
public sealed class EmbeddingOptions
{
    /// <summary>Config section name: <c>Embedding</c>.</summary>
    public const string SectionName = "Embedding";

    /// <summary>
    /// Selected provider: <c>"Ollama"</c>, <c>"OpenAI"</c>, or null/empty to use the Aspire-supplied
    /// Ollama connection (today's behavior). Matched case-insensitively.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>Ollama overrides. Any unset value falls back to the Aspire embeddings connection.</summary>
    public OllamaEmbeddingOptions Ollama { get; set; } = new();

    /// <summary>OpenAI settings. <see cref="OpenAIEmbeddingOptions.ApiKey"/> is required when OpenAI is selected.</summary>
    public OpenAIEmbeddingOptions OpenAI { get; set; } = new();

    /// <summary>Ollama endpoint/model overrides (<c>Embedding:Ollama:*</c>).</summary>
    public sealed class OllamaEmbeddingOptions
    {
        /// <summary>Base URL of the Ollama server; null/empty falls back to the Aspire connection endpoint.</summary>
        public string? Endpoint { get; set; }

        /// <summary>Embedding model name; null/empty falls back to the Aspire connection model.</summary>
        public string? Model { get; set; }
    }

    /// <summary>OpenAI credentials/model (<c>Embedding:OpenAI:*</c>).</summary>
    public sealed class OpenAIEmbeddingOptions
    {
        /// <summary>API key. Never written back to clients in cleartext (masking is SYNTH-22's job).</summary>
        public string? ApiKey { get; set; }

        /// <summary>Embedding model name; null/empty uses a sensible default.</summary>
        public string? Model { get; set; }
    }
}
