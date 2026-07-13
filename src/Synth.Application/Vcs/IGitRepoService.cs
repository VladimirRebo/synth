namespace Synth.Application.Vcs;

/// <summary>
/// Application-layer port for making a remote git repository available as a local checkout, so
/// use cases here (e.g. <see cref="Indexing.IndexRepositoryCommandHandler"/>) can depend on the
/// capability without referencing the concrete <c>GitRepoService</c> in Synth.Infrastructure —
/// Infrastructure references Application, not the other way round, so the port lives here and the
/// implementation realizes it, exactly as <c>IOllamaPullTracker</c> already does for its tracker.
/// </summary>
public interface IGitRepoService
{
    /// <summary>
    /// Returns the local checkout path for <paramref name="repoUrl"/>, cloning it the first time and
    /// fetching + hard-resetting on subsequent calls.
    /// </summary>
    /// <param name="repoUrl">HTTPS (or <c>file://</c>) git remote URL.</param>
    /// <param name="branch">Branch to check out; the repository's default branch when null/empty.</param>
    Task<string> EnsureRepoAsync(string repoUrl, string? branch = null, CancellationToken cancellationToken = default);
}
