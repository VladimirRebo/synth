using Synth.Application.Cqrs;

namespace Synth.Application.Vcs;

/// <summary>
/// Command to process one inbound GitHub webhook delivery — the input to
/// <see cref="ProcessGitHubWebhookCommandHandler"/>, dispatched by <c>POST /webhooks/github</c>.
/// Carries the raw, not-yet-parsed request so signature verification runs over the exact bytes
/// GitHub signed (parsing first and re-serializing could produce different bytes and break the HMAC).
/// </summary>
/// <param name="EventType">The <c>X-GitHub-Event</c> header value, e.g. <c>"push"</c>.</param>
/// <param name="SignatureHeader">The <c>X-Hub-Signature-256</c> header value, or null if absent.</param>
/// <param name="RawBody">The raw UTF-8 request body, exactly as received.</param>
public sealed record ProcessGitHubWebhookCommand(string EventType, string? SignatureHeader, string RawBody)
    : ICommand<ProcessGitHubWebhookResult>;
