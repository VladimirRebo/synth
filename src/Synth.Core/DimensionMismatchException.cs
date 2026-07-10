namespace Synth.Core;

/// <summary>
/// Thrown when an upsert targets an existing vector collection whose embedding dimension differs
/// from the one the current embedding configuration produces. Qdrant enforces a fixed dimension per
/// collection at the gRPC level, so without this guard the mismatch only surfaces as a raw
/// <c>expected dim: N, got M</c> error deep inside the client. Carries both dimensions and the
/// collection name so callers can inspect them programmatically; the <see cref="System.Exception.Message"/>
/// is a clear, actionable instruction that flows through to the indexing job's error surface.
/// </summary>
public sealed class DimensionMismatchException : Exception
{
    public DimensionMismatchException(string collection, int expectedDimension, int actualDimension)
        : base(
            $"Collection '{collection}' was indexed with {expectedDimension}-dimensional embeddings, " +
            $"but the current embedding configuration produces {actualDimension}-dimensional vectors. " +
            "Re-index into a new collection, or switch the embedding provider/model back to a " +
            $"{expectedDimension}-dimensional one.")
    {
        Collection = collection;
        ExpectedDimension = expectedDimension;
        ActualDimension = actualDimension;
    }

    /// <summary>The collection whose stored vector dimension the incoming upsert conflicts with.</summary>
    public string Collection { get; }

    /// <summary>The dimension the collection was originally created with.</summary>
    public int ExpectedDimension { get; }

    /// <summary>The dimension the current embedding configuration produces.</summary>
    public int ActualDimension { get; }
}
