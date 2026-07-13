using System.Text.Json;
using Synth.Application.Cqrs;

namespace Synth.Application.Embeddings;

/// <summary>
/// Command to apply a partial update to the <c>Embedding</c> config section — the input to
/// <see cref="UpdateEmbeddingSettingsCommandHandler"/>, backing <c>PUT /settings/embedding</c>. The raw
/// request body is carried as a <see cref="JsonElement"/> because the probe/partial-update logic depends
/// on a three-way distinction the deserialized shape would lose: a field absent (leave unchanged),
/// present and null (clear), or present and a string (set) — see
/// <see cref="UpdateEmbeddingSettingsCommandHandler"/>. SYNTH-69 lifted the PUT handler's
/// probe-before-persist body out of <c>EmbeddingSettingsEndpoints</c> so it lives behind the CQRS seam
/// (issue #82), following the shape <see cref="Vcs.UpdateVcsSettingsCommand"/> established.
/// </summary>
public sealed record UpdateEmbeddingSettingsCommand(JsonElement Body) : ICommand<UpdateEmbeddingSettingsResult>;
