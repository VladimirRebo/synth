using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Synth.Application.Cqrs;
using Synth.Application.Embeddings;
using Synth.Domain.Embeddings;

namespace Synth.Api.Embeddings;

/// <summary>
/// The <c>Embedding</c> settings endpoints: <c>GET /settings/embedding</c> (read the effective
/// <see cref="EmbeddingOptions"/>, masking the OpenAI API key to a set/not-set flag rather than echoing
/// the secret) and <c>PUT /settings/embedding</c> (partial write). The read stays a thin action over
/// <see cref="IOptionsMonitor{TOptions}"/> — no Query wrapper, same judgment call as
/// <see cref="Synth.Api.Vcs.VcsSettingsController"/>'s reads — while the write's real
/// probe-before-persist logic lives behind the CQRS seam in
/// <see cref="UpdateEmbeddingSettingsCommandHandler"/> (issue #82). A candidate config is probed (build
/// one real embedding for a fixed string) before it is saved; a broken provider is rejected with
/// <c>400</c> and nothing is persisted. SYNTH-69 converted this from the Minimal-API
/// <c>EmbeddingSettingsEndpoints</c> to a Controller (issue #82, slice 14). No auth/RBAC — Synth is a
/// single local user.
/// </summary>
/// <remarks>
/// Routes stay bare (no <c>/api</c> prefix, no class-level <c>[Route]</c>) — each action carries its own
/// absolute route, and the client's Vite proxy strips <c>/api</c>. The Ollama model-picker actions
/// (currently <c>OllamaModelEndpoints</c>, also under <c>/settings/embedding/*</c>) merge into this
/// controller in the next slice.
/// </remarks>
[ApiController]
public class EmbeddingSettingsController : ControllerBase
{
    private readonly ICommandHandler<UpdateEmbeddingSettingsCommand, UpdateEmbeddingSettingsResult> _updateHandler;

    public EmbeddingSettingsController(
        ICommandHandler<UpdateEmbeddingSettingsCommand, UpdateEmbeddingSettingsResult> updateHandler)
    {
        _updateHandler = updateHandler;
    }

    /// <summary>
    /// Current effective <see cref="EmbeddingOptions"/>, with the OpenAI key masked to a set/not-set
    /// flag. Reads <see cref="IOptionsMonitor{TOptions}"/> directly — a plain projection, no command
    /// dispatch.
    /// </summary>
    [HttpGet("/settings/embedding")]
    public IActionResult Get([FromServices] IOptionsMonitor<EmbeddingOptions> options)
        => Ok(EmbeddingSettingsResponse.Mask(options.CurrentValue));

    /// <summary>
    /// Partial update; returns the same masked shape as <see cref="Get"/>. Dispatches an
    /// <see cref="UpdateEmbeddingSettingsCommand"/> carrying the raw body (the probe/partial-update logic
    /// needs the three-way absent/null/string distinction a typed DTO would lose). A validation failure —
    /// a non-object body or a candidate config that failed its embedding probe — maps to <c>400</c> with
    /// the same <c>{ error }</c> shape as before, and nothing is persisted; success returns <c>200</c>
    /// with the masked result.
    /// </summary>
    [HttpPut("/settings/embedding")]
    public async Task<IActionResult> Update([FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        var result = await _updateHandler.HandleAsync(new UpdateEmbeddingSettingsCommand(body), cancellationToken);
        return result.Success
            ? Ok(result.Response)
            : BadRequest(new { error = result.Error });
    }
}
