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

    /// <summary>
    /// How often <c>RepositoryPollingService</c> checks each repoUrl-indexed collection's remote for a
    /// new commit (via <c>git ls-remote</c>, no clone/fetch) and reindexes on a genuine change. Re-read
    /// on every tick, so a change here takes effect without a restart. <c>0</c> (or negative) disables
    /// polling entirely — the service keeps running but just re-checks this value once a minute instead
    /// of ever polling a repository, so re-enabling it later still needs no restart.
    /// </summary>
    public int PollIntervalMinutes { get; set; } = 5;

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
