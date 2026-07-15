using Synth.Domain.Vcs;

namespace Synth.Application.Vcs;

/// <summary>Masked <c>Vcs</c> settings: the raw tokens/secrets are never echoed, only whether one is set.</summary>
public sealed record VcsSettingsResponse(string? WorkspaceRoot, GitHubTokenStatus Github, ProviderTokenStatus Gitlab)
{
    /// <summary>
    /// Projects effective <see cref="VcsOptions"/> into the masked shape returned by both
    /// <c>GET /settings/vcs</c> and a successful <c>PUT</c>: the workspace root verbatim, each provider
    /// token (and GitHub's webhook secret) collapsed to a set/not-set flag so the secret is never echoed.
    /// </summary>
    public static VcsSettingsResponse Mask(VcsOptions options) => new(
        options.WorkspaceRoot,
        new GitHubTokenStatus(
            !string.IsNullOrEmpty(options.GitHub?.Token),
            !string.IsNullOrEmpty(options.GitHub?.WebhookSecret)),
        new ProviderTokenStatus(!string.IsNullOrEmpty(options.GitLab?.Token)));
}

/// <summary>Per-provider token status without the secret value.</summary>
public sealed record ProviderTokenStatus(bool TokenSet);

/// <summary>GitHub's token status plus whether an inbound webhook secret is configured.</summary>
public sealed record GitHubTokenStatus(bool TokenSet, bool WebhookSecretSet);
