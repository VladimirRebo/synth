using Synth.Domain.Vcs;

namespace Synth.Application.Vcs;

/// <summary>
/// Application-layer port for running one repository-poll tick on demand — the same check
/// <c>RepositoryPollingService</c>'s background loop runs on a schedule, exposed here so
/// <c>POST /repositories/poll</c> can trigger it immediately without waiting for the next scheduled
/// tick (which, with the loop's delay-first design, could be up to a full
/// <see cref="VcsOptions.PollIntervalMinutes"/> away — including never, if polling is disabled).
/// </summary>
public interface IRepositoryPoller
{
    /// <summary>
    /// Checks every repoUrl-indexed collection's remote for a new commit and reindexes any that
    /// changed, exactly like one scheduled tick. Returns once every collection has been checked and
    /// any reindex has been <em>dispatched</em> (not necessarily finished — reindexing itself stays
    /// fire-and-forget, same as a manual <c>POST /index</c>).
    /// </summary>
    /// <returns>How many collections had a new commit and got a reindex dispatched.</returns>
    Task<int> PollOnceAsync(CancellationToken cancellationToken = default);
}
