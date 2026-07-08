namespace Synth.Core;

/// <summary>
/// Well-known collection names shared across the store, indexing pipeline and search so every
/// call site agrees on the bucket used when no explicit collection is given. Multi-collection
/// support (SYNTH-17) keys each indexed repository's chunks under its own collection; local-path
/// indexing (<c>POST /index { "path": … }</c>) keeps landing in <see cref="Default"/>, so today's
/// single-corpus behavior is unchanged. Per-repository names arrive in SYNTH-18/19.
/// </summary>
public static class CollectionNames
{
    /// <summary>The collection used when a caller does not specify one.</summary>
    public const string Default = "default";
}
