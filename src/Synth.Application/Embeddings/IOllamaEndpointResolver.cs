namespace Synth.Application.Embeddings;

/// <summary>
/// Resolves the effective Ollama base endpoint — the <c>Embedding:Ollama:Endpoint</c> override when set,
/// otherwise the Aspire-supplied embeddings connection endpoint (null when neither is available). A thin
/// port (issue #82) so <see cref="PullOllamaModelCommandHandler"/> in <c>Synth.Application</c> can resolve
/// the exact same endpoint the live embedding generator talks to without depending on Infrastructure's
/// <c>OllamaConnection</c>/<c>ConfigurableEmbeddingGenerator</c> — Application never references
/// Infrastructure. The implementation reads the live config on each call, so a Settings change is picked up.
/// </summary>
public interface IOllamaEndpointResolver
{
    /// <summary>The current effective Ollama base endpoint, or <c>null</c> when none is configured.</summary>
    string? Resolve();
}
