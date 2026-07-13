namespace Synth.Domain.Graph;

/// <summary>
/// Storage for the structural call graph: directed <see cref="CallEdge"/>s answering "who calls X"
/// and "what does X call" precisely, alongside Synth's approximate vector search. Every operation is
/// scoped by <c>collection</c> (one indexed source) so edges never leak between repos. Backed by a
/// real, doubly-indexed store (SQLite in production, in-memory in tests/dev) — see SYNTH-25 / issue #33.
/// Extraction (SYNTH-26) and query tools (SYNTH-27) build on top of this abstraction.
/// </summary>
public interface ICodeGraphStore
{
    /// <summary>
    /// Replaces <em>all</em> edges for <paramref name="collection"/> with <paramref name="edges"/>
    /// (delete-then-insert). A full replace per index run — not an incremental upsert — so a
    /// re-index never leaves stale edges from methods or calls that no longer exist.
    /// </summary>
    Task ReplaceEdgesAsync(string collection, IReadOnlyList<CallEdge> edges, CancellationToken ct = default);

    /// <summary>Edges whose <see cref="CallEdge.Callee"/> equals <paramref name="symbol"/> within <paramref name="collection"/> — the callers of the symbol.</summary>
    Task<IReadOnlyList<CallEdge>> FindCallersAsync(string collection, string symbol, CancellationToken ct = default);

    /// <summary>Edges whose <see cref="CallEdge.Caller"/> equals <paramref name="symbol"/> within <paramref name="collection"/> — what the symbol calls.</summary>
    Task<IReadOnlyList<CallEdge>> FindCalleesAsync(string collection, string symbol, CancellationToken ct = default);
}
