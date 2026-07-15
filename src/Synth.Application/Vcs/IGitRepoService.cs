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

    /// <summary>
    /// Removes the on-disk checkout for <paramref name="slug"/> (a cloned-remote collection's name),
    /// resolving its workspace-root path and deleting it recursively, tolerating an already-gone
    /// directory. Exposed on the port so <see cref="DeleteCollectionCommandHandler"/> can clean up a
    /// cloned remote's checkout without referencing the concrete <c>GitRepoService</c> — the same
    /// resolve-then-delete the <c>DELETE /repositories/{collection}</c> handler did inline before.
    /// A no-op for a <c>local</c> source, which is never cloned and has no checkout; callers gate on
    /// <c>SourceType</c> before calling.
    /// </summary>
    void RemoveCheckout(string slug);

    /// <summary>
    /// Resolves the on-disk checkout directory for a repoUrl-indexed collection without cloning or
    /// fetching (<paramref name="slug"/> equals the collection name). Exposed on the port so
    /// <c>GetFileTool</c> can read a file out of an already-indexed remote checkout without
    /// referencing the concrete <c>GitRepoService</c>.
    /// </summary>
    string ResolveCheckoutPath(string slug);

    /// <summary>
    /// Cheaply resolves the current HEAD commit SHA of <paramref name="branch"/> (the repository's
    /// default branch when null/empty) on <paramref name="repoUrl"/>'s remote, without cloning or
    /// fetching — a single <c>git ls-remote</c> round trip. Exposed on the port so
    /// <c>RepositoryPollingService</c> can check for a new commit without pulling in the concrete
    /// <c>GitRepoService</c>. Returns null if the remote has no ref matching <paramref name="branch"/>
    /// (e.g. it was renamed or deleted upstream) rather than throwing — a poll tick that can't resolve
    /// one repository should not take down the others.
    /// </summary>
    Task<string?> GetRemoteHeadShaAsync(string repoUrl, string? branch = null, CancellationToken cancellationToken = default);
}
