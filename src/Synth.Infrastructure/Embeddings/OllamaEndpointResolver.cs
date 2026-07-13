using Microsoft.Extensions.Options;
using Synth.Application.Embeddings;
using Synth.Domain.Embeddings;

namespace Synth.Infrastructure.Embeddings;

/// <summary>
/// Infrastructure adapter for <see cref="IOllamaEndpointResolver"/> (issue #82): delegates to
/// <see cref="ConfigurableEmbeddingGenerator.ResolveOllamaEndpoint"/> so the Ollama model-picker/pull
/// resolve the exact same endpoint the live embedding generator talks to — the
/// <c>Embedding:Ollama:Endpoint</c> override when set, otherwise the Aspire-supplied embeddings connection.
/// Reads <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> on each call so a live Settings change is
/// picked up.
/// </summary>
public sealed class OllamaEndpointResolver : IOllamaEndpointResolver
{
    private readonly IOptionsMonitor<EmbeddingOptions> _options;
    private readonly OllamaConnection _aspireDefault;

    public OllamaEndpointResolver(IOptionsMonitor<EmbeddingOptions> options, OllamaConnection aspireDefault)
    {
        _options = options;
        _aspireDefault = aspireDefault;
    }

    public string? Resolve() =>
        ConfigurableEmbeddingGenerator.ResolveOllamaEndpoint(_options.CurrentValue, _aspireDefault);
}
