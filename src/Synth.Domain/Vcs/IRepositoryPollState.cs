namespace Synth.Domain.Vcs;

/// <summary>
/// Tracks the last remote HEAD commit SHA <c>RepositoryPollingService</c> observed for each
/// repoUrl-indexed collection, so a poll tick can tell "unchanged" from "new commit, reindex" without
/// re-deriving it from scratch every time. Deliberately separate from <see cref="IRepositoryRegistry"/>
/// — <see cref="RepositoryEntry"/> models what is indexed; this models the polling mechanism's own
/// bookkeeping, which has no meaning outside that mechanism.
/// </summary>
public interface IRepositoryPollState
{
    /// <summary>The last SHA observed for <paramref name="collection"/>, or null if it has never been polled.</summary>
    Task<string?> GetLastKnownShaAsync(string collection, CancellationToken cancellationToken = default);

    /// <summary>Records <paramref name="sha"/> as the last observed SHA for <paramref name="collection"/>.</summary>
    Task SetLastKnownShaAsync(string collection, string sha, CancellationToken cancellationToken = default);
}
