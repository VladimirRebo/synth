namespace Synth.Application.Configuration;

/// <summary>
/// Result of handling a <see cref="ReplaceRawSettingsCommand"/>. Either a validation failure (a
/// malformed or non-object JSON body) carrying a human-readable <see cref="Error"/> — which the
/// controller maps to <c>400</c> with the same <c>{ error }</c> shape as before, nothing persisted —
/// or success carrying both the persisted <see cref="Document"/> text (echoed unmasked as the response
/// body) and the non-fatal <see cref="Warnings"/> the controller surfaces via the
/// <c>X-Settings-Warnings</c> header. Success needs both because the write has already landed: the
/// document is the response body, the warnings are purely informational about what was just stored.
/// </summary>
public sealed record ReplaceRawSettingsResult(
    bool Success, string? Document, IReadOnlyList<string> Warnings, string? Error)
{
    public static ReplaceRawSettingsResult Ok(string document, IReadOnlyList<string> warnings) =>
        new(true, document, warnings, null);

    public static ReplaceRawSettingsResult ValidationError(string message) =>
        new(false, null, Array.Empty<string>(), message);
}
