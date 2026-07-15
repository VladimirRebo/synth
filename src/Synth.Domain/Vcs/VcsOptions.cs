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

    /// <summary>Auth for GitHub remotes (<c>Vcs:GitHub:Token</c>).</summary>
    public ProviderAuth GitHub { get; set; } = new();

    /// <summary>Auth for GitLab remotes (<c>Vcs:GitLab:Token</c>).</summary>
    public ProviderAuth GitLab { get; set; } = new();

    /// <summary>Per-provider access token. Never written to disk — passed to git via an in-memory header.</summary>
    public sealed class ProviderAuth
    {
        /// <summary>Personal/access token, or null for anonymous access.</summary>
        public string? Token { get; set; }
    }
}
