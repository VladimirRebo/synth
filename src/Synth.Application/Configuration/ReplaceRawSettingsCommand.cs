using Synth.Application.Cqrs;

namespace Synth.Application.Configuration;

/// <summary>
/// Command to replace the <b>entire</b> stored config document with a raw JSON body — the input to
/// <see cref="ReplaceRawSettingsCommandHandler"/>, backing <c>PUT /settings/raw</c>. The body is
/// carried as the verbatim request text (not a re-serialized <see cref="System.Text.Json.JsonElement"/>)
/// so what a subsequent <c>GET</c> returns is byte-for-byte what was sent — the whole point of the raw
/// editor escape hatch. SYNTH-71 lifted the PUT handler's replace-and-warn body out of
/// <c>RawSettingsEndpoints</c> so it lives behind the CQRS seam (issue #82), following the pattern
/// <see cref="Vcs.UpdateVcsSettingsCommand"/> established.
/// </summary>
public sealed record ReplaceRawSettingsCommand(string Body) : ICommand<ReplaceRawSettingsResult>;
