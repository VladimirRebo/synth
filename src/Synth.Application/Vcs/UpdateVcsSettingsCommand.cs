using System.Text.Json;
using Synth.Application.Cqrs;

namespace Synth.Application.Vcs;

/// <summary>
/// Command to apply a partial update to the <c>Vcs</c> config section — the input to
/// <see cref="UpdateVcsSettingsCommandHandler"/>, backing <c>PUT /settings/vcs</c>. The raw request
/// body is carried as a <see cref="JsonElement"/> because the probe/partial-update logic depends on a
/// three-way distinction the deserialized shape would lose: a field absent (leave unchanged), present
/// and null (clear), or present and a string (set) — see <see cref="UpdateVcsSettingsCommandHandler"/>.
/// SYNTH-68 lifted the PUT handler's probe-before-persist body out of <c>VcsSettingsEndpoints</c> so it
/// lives behind the CQRS seam (issue #82), following the pattern <c>IndexRepositoryCommand</c> and
/// <c>DeleteCollectionCommand</c> established.
/// </summary>
public sealed record UpdateVcsSettingsCommand(JsonElement Body) : ICommand<UpdateVcsSettingsResult>;
