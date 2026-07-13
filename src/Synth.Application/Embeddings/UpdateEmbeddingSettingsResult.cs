namespace Synth.Application.Embeddings;

/// <summary>
/// Result of handling an <see cref="UpdateEmbeddingSettingsCommand"/>. The PUT is a probe-before-persist
/// flow, so the outcome is either a validation failure (a non-object body, or a candidate config whose
/// embedding probe failed) carrying a human-readable <see cref="Error"/> — which the controller maps to
/// <c>400</c> with the same <c>{ error }</c> shape as before — or success carrying the masked
/// <see cref="Response"/> built from the just-reloaded <c>IOptionsMonitor&lt;EmbeddingOptions&gt;</c>.
/// Mirrors <see cref="Vcs.UpdateVcsSettingsResult"/>.
/// </summary>
public sealed record UpdateEmbeddingSettingsResult(bool Success, EmbeddingSettingsResponse? Response, string? Error)
{
    public static UpdateEmbeddingSettingsResult Ok(EmbeddingSettingsResponse response) => new(true, response, null);

    public static UpdateEmbeddingSettingsResult ValidationError(string message) => new(false, null, message);
}
