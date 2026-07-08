namespace Synth.Core.Vcs;

/// <summary>
/// Coarse classification of a git remote's hosting provider, derived from the URL host.
/// Only distinguishes the two providers Synth authenticates against differently (GitHub uses an
/// <c>Authorization: Bearer</c> header, GitLab a <c>PRIVATE-TOKEN</c> header); everything else is
/// <see cref="Other"/> and treated as an unauthenticated public remote.
/// </summary>
public enum GitProvider
{
    /// <summary>Host contains <c>"github"</c> (github.com or a self-hosted GitHub Enterprise).</summary>
    GitHub,

    /// <summary>Host contains <c>"gitlab"</c> (gitlab.com or a self-hosted GitLab).</summary>
    GitLab,

    /// <summary>Any other host — no provider-specific auth is applied.</summary>
    Other,
}
