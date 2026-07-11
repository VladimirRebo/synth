using System.Collections.Concurrent;
using Synth.Domain.Graph;

namespace Synth.Api.Graph;

/// <summary>
/// Process-local <see cref="ICodeGraphStore"/> used when no Mongo connection is configured — the
/// same "degrade gracefully when Mongo is absent" fallback that <c>InMemoryRepositoryRegistry</c> is
/// to <c>MongoRepositoryRegistry</c>. Keeps tests and Docker-less local dev running without a live
/// Mongo. Edges are grouped by collection so per-collection replace and lookups never touch another
/// collection's edges.
/// </summary>
public sealed class InMemoryCodeGraphStore : ICodeGraphStore
{
    private readonly ConcurrentDictionary<string, List<CallEdge>> _byCollection = new();

    public Task ReplaceEdgesAsync(string collection, IReadOnlyList<CallEdge> edges, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(edges);

        // Full replace: overwrite the whole bucket so nothing from a previous index run survives.
        _byCollection[collection] = [.. edges];
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CallEdge>> FindCallersAsync(string collection, string symbol, CancellationToken ct = default) =>
        Task.FromResult(Query(collection, edge => edge.Callee == symbol));

    public Task<IReadOnlyList<CallEdge>> FindCalleesAsync(string collection, string symbol, CancellationToken ct = default) =>
        Task.FromResult(Query(collection, edge => edge.Caller == symbol));

    private IReadOnlyList<CallEdge> Query(string collection, Func<CallEdge, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (!_byCollection.TryGetValue(collection, out var edges))
            return [];

        // A stored bucket is never mutated in place — ReplaceEdgesAsync swaps in a fresh list — so
        // scanning the reference we fetched is safe even if a replace runs concurrently.
        return edges.Where(predicate).ToList();
    }
}
