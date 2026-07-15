using Synth.Domain.Vcs;

namespace Synth.Application.Vcs;

/// <summary>Masked <c>Vcs</c> settings: the raw tokens are never echoed, only whether one is set.</summary>
public sealed record VcsSettingsResponse(
    string? WorkspaceRoot, int PollIntervalMinutes, ProviderTokenStatus Github, ProviderTokenStatus Gitlab)
{
    /// <summary>
    /// Projects effective <see cref="VcsOptions"/> into the masked shape returned by both
    /// <c>GET /settings/vcs</c> and a successful <c>PUT</c>: the workspace root and poll interval
    /// verbatim (neither is a secret), each provider token collapsed to a set/not-set flag so the
    /// secret is never echoed.
    /// </summary>
    public static VcsSettingsResponse Mask(VcsOptions options) => new(
        options.WorkspaceRoot,
        options.PollIntervalMinutes,
        new ProviderTokenStatus(!string.IsNullOrEmpty(options.GitHub?.Token)),
        new ProviderTokenStatus(!string.IsNullOrEmpty(options.GitLab?.Token)));
}

/// <summary>Per-provider token status without the secret value.</summary>
public sealed record ProviderTokenStatus(bool TokenSet);
