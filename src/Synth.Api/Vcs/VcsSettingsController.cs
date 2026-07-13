using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Synth.Application.Cqrs;
using Synth.Application.Vcs;
using Synth.Domain.Vcs;

namespace Synth.Api.Vcs;

/// <summary>
/// The <c>Vcs</c> settings endpoints: <c>GET /settings/vcs</c> (read the effective
/// <see cref="VcsOptions"/>, masking each provider token to a set/not-set flag rather than echoing the
/// secret) and <c>PUT /settings/vcs</c> (partial write). The read stays a thin action over
/// <see cref="IOptionsMonitor{TOptions}"/> — no Query wrapper, same judgment call as
/// <see cref="Synth.Api.Search.SearchController"/>'s reads — while the write's real
/// probe-before-persist logic lives behind the CQRS seam in
/// <see cref="UpdateVcsSettingsCommandHandler"/> (issue #82). A newly-set, non-empty GitHub/GitLab
/// token is probed against the provider's API before it is saved; a bad token is rejected with
/// <c>400</c> and nothing is persisted. SYNTH-68 converted this from the Minimal-API
/// <c>VcsSettingsEndpoints</c> to a Controller (issue #82, slice 13). No auth/RBAC — Synth is a single
/// local user.
/// </summary>
/// <remarks>
/// Routes stay bare (no <c>/api</c> prefix, no class-level <c>[Route]</c>) — each action carries its
/// own absolute route, and the client's Vite proxy strips <c>/api</c>.
/// </remarks>
[ApiController]
public class VcsSettingsController : ControllerBase
{
    private readonly ICommandHandler<UpdateVcsSettingsCommand, UpdateVcsSettingsResult> _updateHandler;

    public VcsSettingsController(ICommandHandler<UpdateVcsSettingsCommand, UpdateVcsSettingsResult> updateHandler)
    {
        _updateHandler = updateHandler;
    }

    /// <summary>
    /// Current effective <see cref="VcsOptions"/>, with tokens masked to a set/not-set flag. Reads
    /// <see cref="IOptionsMonitor{TOptions}"/> directly — a plain projection, no command dispatch.
    /// </summary>
    [HttpGet("/settings/vcs")]
    public IActionResult Get([FromServices] IOptionsMonitor<VcsOptions> options)
        => Ok(VcsSettingsResponse.Mask(options.CurrentValue));

    /// <summary>
    /// Partial update; returns the same masked shape as <see cref="Get"/>. Dispatches an
    /// <see cref="UpdateVcsSettingsCommand"/> carrying the raw body (the probe/partial-update logic needs
    /// the three-way absent/null/string distinction a typed DTO would lose). A validation failure — a
    /// non-object body or a newly-set token that failed its provider probe — maps to <c>400</c> with the
    /// same <c>{ error }</c> shape as before, and nothing is persisted; success returns <c>200</c> with
    /// the masked result.
    /// </summary>
    [HttpPut("/settings/vcs")]
    public async Task<IActionResult> Update([FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        var result = await _updateHandler.HandleAsync(new UpdateVcsSettingsCommand(body), cancellationToken);
        return result.Success
            ? Ok(result.Response)
            : BadRequest(new { error = result.Error });
    }
}
