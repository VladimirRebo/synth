using System.Collections.Concurrent;
using Synth.Domain.Vcs;

namespace Synth.Infrastructure.Vcs;

/// <summary>
/// Process-local <see cref="IRepositoryRegistry"/> used when no Mongo connection is configured —
/// the same "degrade gracefully when Mongo is absent" fallback that <c>FileConfigStore</c> is to
/// <c>MongoConfigStore</c>. Keeps tests and Docker-less local dev running without a live Mongo.
/// </summary>
public sealed class InMemoryRepositoryRegistry : IRepositoryRegistry
{
    private readonly ConcurrentDictionary<string, RepositoryEntry> _entries = new();

    public Task UpsertAsync(RepositoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries[entry.Collection] = entry;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RepositoryEntry>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RepositoryEntry>>(_entries.Values.ToList());

    public Task<bool> DeleteAsync(string collection, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        return Task.FromResult(_entries.TryRemove(collection, out _));
    }
}
