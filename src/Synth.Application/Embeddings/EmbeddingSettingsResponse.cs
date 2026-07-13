using System.Text.Json.Serialization;
using Synth.Domain.Embeddings;

namespace Synth.Application.Embeddings;

/// <summary>Masked <c>Embedding</c> settings: the OpenAI API key is never echoed, only whether one is set.</summary>
public sealed record EmbeddingSettingsResponse(
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("ollama")] OllamaSettingsView Ollama,
    [property: JsonPropertyName("openai")] OpenAISettingsView OpenAI)
{
    /// <summary>
    /// Projects effective <see cref="EmbeddingOptions"/> into the masked shape returned by both
    /// <c>GET /settings/embedding</c> and a successful <c>PUT</c>: the provider verbatim, the Ollama
    /// endpoint/model verbatim, and the OpenAI key collapsed to a set/not-set flag so the secret is
    /// never echoed.
    /// </summary>
    public static EmbeddingSettingsResponse Mask(EmbeddingOptions options) => new(
        string.IsNullOrWhiteSpace(options.Provider) ? null : options.Provider,
        new OllamaSettingsView(options.Ollama.Endpoint, options.Ollama.Model),
        new OpenAISettingsView(!string.IsNullOrEmpty(options.OpenAI.ApiKey), options.OpenAI.Model));
}

/// <summary>Ollama endpoint/model overrides as reported by GET (both may be null → Aspire fallback).</summary>
public sealed record OllamaSettingsView(
    [property: JsonPropertyName("endpoint")] string? Endpoint,
    [property: JsonPropertyName("model")] string? Model);

/// <summary>OpenAI settings without the secret: only whether a key is set, plus the model.</summary>
public sealed record OpenAISettingsView(
    [property: JsonPropertyName("apiKeySet")] bool ApiKeySet,
    [property: JsonPropertyName("model")] string? Model);
