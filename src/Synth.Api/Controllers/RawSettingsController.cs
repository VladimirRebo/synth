using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Synth.Application.Configuration;
using Synth.Application.Cqrs;

namespace Synth.Api.Controllers;

/// <summary>
/// The raw-settings endpoints: <c>GET /settings/raw</c> (read the whole stored config document as-is,
/// secrets included <b>unmasked</b>) and <c>PUT /settings/raw</c> (replace the whole document). This is
/// an advanced escape hatch — the opposite of the per-section endpoints (<c>/settings/vcs</c>,
/// <c>/settings/embedding</c>), which mask secrets for the common case. Synth is a single local user
/// with no auth and the values already sit in plaintext in Mongo/the config file, so this is a UX
/// affordance, not a broken security boundary (2026-07-09 decision, SYNTH-29).
/// <para>
/// The read stays a thin action over <see cref="IConfigSectionUpdater.LoadDocumentAsync"/> — no Query
/// wrapper, same judgment call as <see cref="VcsSettingsController"/>'s reads — while the
/// write's replace-and-warn logic lives behind the CQRS seam in
/// <see cref="ReplaceRawSettingsCommandHandler"/> (issue #82). Unlike <c>/settings/embedding</c> there is
/// deliberately <b>no probe-before-persist</b>: a malformed or non-object body is rejected with
/// <c>400</c>, otherwise the exact bytes sent are persisted through the same reload path the section
/// endpoints rely on. SYNTH-71 converted this from the Minimal-API <c>RawSettingsEndpoints</c> to a
/// Controller (issue #82, slice 16).
/// </para>
/// </summary>
/// <remarks>
/// Routes stay bare (no <c>/api</c> prefix, no class-level <c>[Route]</c>) — each action carries its
/// own absolute route, and the client's Vite proxy strips <c>/api</c>.
/// </remarks>
[ApiController]
public class RawSettingsController : ControllerBase
{
    // Response header carrying non-fatal warnings about a persisted document (currently: top-level
    // keys that match no config section this app reads). A header keeps the PUT body the bare
    // document text — the raw editor client consumes the response via response.text() — so surfacing
    // warnings this way is purely additive and doesn't break that integration.
    public const string WarningsHeader = "X-Settings-Warnings";

    private readonly ICommandHandler<ReplaceRawSettingsCommand, ReplaceRawSettingsResult> _replaceHandler;

    public RawSettingsController(
        ICommandHandler<ReplaceRawSettingsCommand, ReplaceRawSettingsResult> replaceHandler)
    {
        _replaceHandler = replaceHandler;
    }

    /// <summary>
    /// The whole stored document as-is (unmasked), <c>"{}"</c> when nothing is stored. Reads
    /// <see cref="IConfigSectionUpdater.LoadDocumentAsync"/> directly — a plain projection, no command
    /// dispatch.
    /// </summary>
    [HttpGet("/settings/raw")]
    public async Task<IActionResult> Get(
        [FromServices] IConfigSectionUpdater updater, CancellationToken cancellationToken)
    {
        var document = await updater.LoadDocumentAsync(cancellationToken);
        return Content(document, "application/json");
    }

    /// <summary>
    /// Replace the whole document. Reads the raw request body verbatim (so a subsequent <c>GET</c>
    /// returns exactly what was sent) rather than binding a <see cref="JsonElement"/> and
    /// re-serializing, and dispatches a <see cref="ReplaceRawSettingsCommand"/>. A malformed or
    /// non-object body maps to <c>400</c> with the same <c>{ error }</c> shape as before and nothing is
    /// persisted; on success the persisted document is echoed as the body and any non-fatal warnings
    /// are surfaced via the <see cref="WarningsHeader"/> header.
    /// </summary>
    [HttpPut("/settings/raw")]
    public async Task<IActionResult> Put(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);

        var result = await _replaceHandler.HandleAsync(new ReplaceRawSettingsCommand(body), cancellationToken);
        if (!result.Success)
            return BadRequest(new { error = result.Error });

        if (result.Warnings.Count > 0)
        {
            // Serialized as a JSON array; default escaping keeps it header-safe (ASCII) even for
            // non-ASCII key names. Kept out of the body so the response stays the bare document text.
            Response.Headers[WarningsHeader] = JsonSerializer.Serialize(result.Warnings);
        }

        return Content(result.Document!, "application/json");
    }
}
