namespace Synth.Domain.Vcs;

/// <summary>
/// Configuration for cloning/fetching remote repositories, bound from the <c>Vcs</c> config section
/// through the existing layered <c>IConfigStore</c>/<c>IOptionsMonitor</c> machinery (the actual
/// <c>services.Configure&lt;VcsOptions&gt;(...)</c> wiring lands in SYNTH-19). Consumed as
/// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/> so a token added at runtime
/// is picked up without a restart. All values are optional — public repos work with none set.
/// </summary>
public sealed class VcsOptions
{
    /// <summary>Config section name: <c>Vcs</c>.</summary>
    public const string SectionName = "Vcs";

    /// <summary>
    /// Directory that holds one checkout subdirectory per repository slug. When null/empty,
    /// <see cref="GitRepoService"/> defaults to <c>~/.synth/workspaces</c>.
    /// </summary>
    public string? WorkspaceRoot { get; set; }

    /// <summary>Auth for GitHub remotes (<c>Vcs:GitHub:Token</c>), plus the inbound webhook secret.</summary>
    public GitHubAuth GitHub { get; set; } = new();

    /// <summary>Auth for GitLab remotes (<c>Vcs:GitLab:Token</c>).</summary>
    public ProviderAuth GitLab { get; set; } = new();

    /// <summary>Per-provider access token. Never written to disk — passed to git via an in-memory header.</summary>
    public class ProviderAuth
    {
        /// <summary>Personal/access token, or null for anonymous access.</summary>
        public string? Token { get; set; }
    }

    /// <summary>
    /// GitHub-specific auth: the outbound clone/fetch <see cref="ProviderAuth.Token"/> plus the
    /// inbound <see cref="WebhookSecret"/> used to verify <c>X-Hub-Signature-256</c> on
    /// <c>POST /webhooks/github</c> — two unrelated secrets that happen to share a provider block.
    /// GitLab has no webhook consumer yet, so this stays GitHub-only rather than adding an unused
    /// field to the shared <see cref="ProviderAuth"/>.
    /// </summary>
    public sealed class GitHubAuth : ProviderAuth
    {
        /// <summary>
        /// Shared secret configured in the GitHub repo's Settings → Webhooks → Secret, used to verify
        /// the <c>X-Hub-Signature-256</c> header via constant-time HMAC-SHA256 comparison. Null/empty
        /// means webhook delivery is rejected outright — there is no legitimate "unauthenticated
        /// webhook" mode.
        /// </summary>
        public string? WebhookSecret { get; set; }
    }
}
