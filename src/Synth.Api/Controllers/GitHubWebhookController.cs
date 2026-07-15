using Microsoft.AspNetCore.Mvc;
using Synth.Application.Cqrs;
using Synth.Application.Vcs;

namespace Synth.Api.Controllers;

/// <summary>
/// <c>POST /webhooks/github</c>: the delivery endpoint a GitHub repo's Settings → Webhooks points at.
/// Thin by necessity, not by choice — the real signature-verification/branch-matching/dispatch logic
/// lives behind the CQRS seam in <see cref="ProcessGitHubWebhookCommandHandler"/>, same pattern as
/// every other write endpoint in this API. Reads the body as raw text (not <c>[FromBody]</c> model
/// binding) because HMAC signature verification needs the exact bytes GitHub signed — parsing and
/// re-serializing JSON is not guaranteed to round-trip byte-for-byte.
/// </summary>
/// <remarks>
/// Route stays bare (no <c>/api</c> prefix) like every other endpoint — GitHub calls this directly, so
/// the Vite dev proxy's <c>/api</c>-stripping is irrelevant here, but the convention still applies for
/// consistency with the rest of the API surface.
/// </remarks>
[ApiController]
public class GitHubWebhookController : ControllerBase
{
    private readonly ICommandHandler<ProcessGitHubWebhookCommand, ProcessGitHubWebhookResult> _handler;

    public GitHubWebhookController(
        ICommandHandler<ProcessGitHubWebhookCommand, ProcessGitHubWebhookResult> handler)
    {
        _handler = handler;
    }

    /// <summary>
    /// Verifies <c>X-Hub-Signature-256</c> against the configured webhook secret, then — for a
    /// <c>push</c> to the branch a matching collection was indexed from — starts the same reindex
    /// <c>POST /index</c> would. Always <c>200</c> once authenticated, even when the delivery didn't
    /// result in a reindex (wrong event, unmatched repo, wrong branch, already running): from GitHub's
    /// side the delivery was received and handled, and a non-2xx would just trigger a pointless retry.
    /// A missing/invalid signature — or no secret configured at all — is <c>401</c>.
    /// </summary>
    [HttpPost("/webhooks/github")]
    public async Task<IActionResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        string rawBody;
        using (var reader = new StreamReader(Request.Body))
            rawBody = await reader.ReadToEndAsync(cancellationToken);

        var eventType = Request.Headers["X-GitHub-Event"].ToString();
        var signature = Request.Headers["X-Hub-Signature-256"].ToString();

        var result = await _handler.HandleAsync(
            new ProcessGitHubWebhookCommand(eventType, string.IsNullOrEmpty(signature) ? null : signature, rawBody),
            cancellationToken);

        return result.Status == ProcessGitHubWebhookResult.Kind.Unauthorized
            ? Unauthorized(new { error = result.Message })
            : Ok(new { status = result.Status.ToString(), message = result.Message });
    }
}
