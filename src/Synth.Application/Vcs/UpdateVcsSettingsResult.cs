namespace Synth.Application.Vcs;

/// <summary>
/// Result of handling an <see cref="UpdateVcsSettingsCommand"/>. The PUT is a probe-before-persist
/// flow, so the outcome is either a validation failure (a non-object body, or a newly-set token that
/// failed its provider probe) carrying a human-readable <see cref="Error"/> — which the controller maps
/// to <c>400</c> with the same <c>{ error }</c> shape as before — or success carrying the masked
/// <see cref="Response"/> built from the just-reloaded <c>IOptionsMonitor&lt;VcsOptions&gt;</c>.
/// </summary>
public sealed record UpdateVcsSettingsResult(bool Success, VcsSettingsResponse? Response, string? Error)
{
    public static UpdateVcsSettingsResult Ok(VcsSettingsResponse response) => new(true, response, null);

    public static UpdateVcsSettingsResult ValidationError(string message) => new(false, null, message);
}
