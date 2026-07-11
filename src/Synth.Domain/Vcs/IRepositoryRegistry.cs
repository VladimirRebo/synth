namespace Synth.Domain.Vcs;

/// <summary>
/// Registry of indexed sources: one <see cref="RepositoryEntry"/> per collection, so callers
/// (the Vue client's collection picker in SYNTH-20, and <c>search_code</c>/<c>GET /search</c>
/// callers that need valid collection names) can discover what has been indexed. Populated after
/// each successful <c>POST /index</c> run.
/// </summary>
public interface IRepositoryRegistry
{
    /// <summary>Inserts or replaces the entry for <see cref="RepositoryEntry.Collection"/>.</summary>
    Task UpsertAsync(RepositoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Lists every known collection and its metadata.</summary>
    Task<IReadOnlyList<RepositoryEntry>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the entry for <paramref name="collection"/>. Returns <c>true</c> if an entry existed
    /// and was removed, <c>false</c> if there was no entry for that collection (a no-op) — the caller
    /// uses this to answer <c>DELETE /repositories/{collection}</c> with 404 for an unknown collection.
    /// </summary>
    Task<bool> DeleteAsync(string collection, CancellationToken cancellationToken = default);
}

/// <summary>
/// Metadata about one indexed source, keyed by <see cref="Collection"/> (the vector-store
/// collection its chunks live in).
/// </summary>
public sealed record RepositoryEntry
{
    /// <summary>Vector-store collection holding this source's chunks (the registry key).</summary>
    public required string Collection { get; init; }

    /// <summary>Origin of the source: <c>local</c>, <c>github</c>, <c>gitlab</c> or <c>other</c>.</summary>
    public required string SourceType { get; init; }

    /// <summary>The local path (for <c>local</c>) or remote URL (for a git source) that was indexed.</summary>
    public required string Source { get; init; }

    /// <summary>Branch indexed for a git source; null for local-path indexing or the default branch.</summary>
    public string? Branch { get; init; }

    /// <summary>UTC timestamp of the most recent successful index run for this collection.</summary>
    public DateTime LastIndexedAt { get; init; }

    /// <summary>Number of chunks upserted by the most recent index run.</summary>
    public int ChunkCount { get; init; }
}
