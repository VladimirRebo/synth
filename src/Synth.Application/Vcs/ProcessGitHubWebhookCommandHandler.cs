using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Synth.Application.Cqrs;
using Synth.Application.Indexing;
using Synth.Domain.Vcs;

namespace Synth.Application.Vcs;

/// <summary>
/// Handles <see cref="ProcessGitHubWebhookCommand"/>: verify the delivery is genuinely from GitHub,
/// then — for a <c>push</c> to the branch an existing collection was indexed from — dispatch the same
/// <see cref="IndexRepositoryCommand"/> <c>POST /index</c> and <c>index_code</c> already use, so a
/// webhook-triggered reindex is identical in every way to a manually-triggered one.
/// </summary>
/// <remarks>
/// The collection to reindex is resolved by re-deriving <see cref="RepoUrlInfo.Slug"/> from the
/// payload's <c>repository.clone_url</c> and matching it against <see cref="IRepositoryRegistry"/>'s
/// <see cref="RepositoryEntry.Collection"/> — the same slug <see cref="IndexRepositoryCommandHandler"/>
/// derives when a repo URL is first indexed, so no separate URL-to-collection mapping is needed.
/// <para>
/// No queue: if a job is already running (<see cref="IIndexJobTracker"/>'s single slot),
/// this delivery is simply reported as <see cref="ProcessGitHubWebhookResult.Kind.AlreadyRunning"/> and
/// dropped — the next push (or a manual reindex) will catch up. Queueing missed pushes is deliberately
/// out of scope for this first cut.
/// </para>
/// </remarks>
public sealed class ProcessGitHubWebhookCommandHandler
    : ICommandHandler<ProcessGitHubWebhookCommand, ProcessGitHubWebhookResult>
{
    private const string PushEventType = "push";
    private const string BranchRefPrefix = "refs/heads/";
    private const string SignaturePrefix = "sha256=";

    private readonly IOptionsMonitor<VcsOptions> _options;
    private readonly IRepositoryRegistry _registry;
    private readonly ICommandHandler<IndexRepositoryCommand, IndexStartOutcome> _indexHandler;

    public ProcessGitHubWebhookCommandHandler(
        IOptionsMonitor<VcsOptions> options,
        IRepositoryRegistry registry,
        ICommandHandler<IndexRepositoryCommand, IndexStartOutcome> indexHandler)
    {
        _options = options;
        _registry = registry;
        _indexHandler = indexHandler;
    }

    public async Task<ProcessGitHubWebhookResult> HandleAsync(
        ProcessGitHubWebhookCommand command, CancellationToken cancellationToken = default)
    {
        var secret = _options.CurrentValue.GitHub?.WebhookSecret;
        if (string.IsNullOrEmpty(secret))
            return ProcessGitHubWebhookResult.Unauthorized("No GitHub webhook secret is configured.");

        if (!IsValidSignature(command.RawBody, command.SignatureHeader, secret))
            return ProcessGitHubWebhookResult.Unauthorized("Signature verification failed.");

        if (!string.Equals(command.EventType, PushEventType, StringComparison.Ordinal))
        {
            return ProcessGitHubWebhookResult.Ignored(
                $"Ignoring '{command.EventType}' event; only 'push' triggers a reindex.");
        }

        PushPayload payload;
        try
        {
            payload = ParsePayload(command.RawBody);
        }
        catch (JsonException)
        {
            return ProcessGitHubWebhookResult.Ignored("Payload is not valid JSON.");
        }

        if (payload.CloneUrl is null || !RepoUrlInfo.TryParse(payload.CloneUrl, out var repoInfo))
            return ProcessGitHubWebhookResult.Ignored("Could not determine the repository URL from the payload.");

        if (payload.Ref is null || !payload.Ref.StartsWith(BranchRefPrefix, StringComparison.Ordinal))
            return ProcessGitHubWebhookResult.Ignored("Not a branch push (tag or other ref); nothing to reindex.");

        var pushedBranch = payload.Ref[BranchRefPrefix.Length..];

        var entries = await _registry.ListAsync(cancellationToken);
        var entry = entries.FirstOrDefault(e => string.Equals(e.Collection, repoInfo!.Slug, StringComparison.Ordinal));
        if (entry is null)
            return ProcessGitHubWebhookResult.Ignored($"No indexed collection matches '{repoInfo!.Slug}'.");

        // A null entry.Branch means "the default branch was indexed" (see RepositoryEntry.Branch) — the
        // payload's own default_branch tells us what that resolves to for this push.
        var expectedBranch = entry.Branch ?? payload.DefaultBranch;
        if (!string.Equals(pushedBranch, expectedBranch, StringComparison.Ordinal))
        {
            return ProcessGitHubWebhookResult.Ignored(
                $"Push to '{pushedBranch}' does not match the indexed branch '{expectedBranch ?? "(unknown)"}'.");
        }

        var outcome = await _indexHandler.HandleAsync(
            new IndexRepositoryCommand(RepoUrl: entry.Source, Branch: entry.Branch), cancellationToken);

        return outcome.Status switch
        {
            IndexStartOutcome.Kind.Started => ProcessGitHubWebhookResult.Started(outcome.Collection!),
            IndexStartOutcome.Kind.AlreadyRunning => ProcessGitHubWebhookResult.AlreadyRunning(outcome.Error!),
            _ => ProcessGitHubWebhookResult.Ignored(outcome.Error ?? "Could not start reindexing."),
        };
    }

    // Recomputes the HMAC-SHA256 of the raw body under the configured secret and compares it against
    // the "sha256=<hex>" header in constant time — a non-constant-time compare would let an attacker
    // incrementally forge a valid signature by timing how many leading hex characters matched.
    private static bool IsValidSignature(string rawBody, string? signatureHeader, string secret)
    {
        if (string.IsNullOrEmpty(signatureHeader) ||
            !signatureHeader.StartsWith(SignaturePrefix, StringComparison.Ordinal))
            return false;

        var providedHex = signatureHeader[SignaturePrefix.Length..];
        var computed = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(rawBody));
        var computedHex = Convert.ToHexStringLower(computed);

        return providedHex.Length == computedHex.Length &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(providedHex), Encoding.ASCII.GetBytes(computedHex));
    }

    // Pulls just the handful of fields a push event needs out of GitHub's payload — everything else
    // (commits, pusher, sender, ...) is irrelevant to "should this trigger a reindex".
    private static PushPayload ParsePayload(string rawBody)
    {
        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;

        string? cloneUrl = null;
        string? defaultBranch = null;
        if (root.TryGetProperty("repository", out var repository))
        {
            if (repository.TryGetProperty("clone_url", out var cloneUrlElement))
                cloneUrl = cloneUrlElement.GetString();
            if (repository.TryGetProperty("default_branch", out var defaultBranchElement))
                defaultBranch = defaultBranchElement.GetString();
        }

        var refValue = root.TryGetProperty("ref", out var refElement) ? refElement.GetString() : null;

        return new PushPayload(cloneUrl, refValue, defaultBranch);
    }

    private sealed record PushPayload(string? CloneUrl, string? Ref, string? DefaultBranch);
}
