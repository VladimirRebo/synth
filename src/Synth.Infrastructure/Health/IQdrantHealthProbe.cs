using Qdrant.Client;

namespace Synth.Infrastructure.Health;

/// <summary>
/// A thin seam over "is Qdrant reachable?". A real <see cref="QdrantClient"/> is a sealed concrete
/// type whose methods can't be substituted in a unit test, so <see cref="HealthCheckService"/> depends
/// on this interface instead: production wraps the DI-registered client (<see cref="QdrantHealthProbe"/>),
/// the in-memory fallback reports healthy (<see cref="NotConfiguredQdrantHealthProbe"/>), and tests fake it.
/// </summary>
public interface IQdrantHealthProbe
{
    /// <summary>Performs a lightweight round trip to Qdrant, throwing if it is unreachable.</summary>
    Task CheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Production probe: lists collections, the cheapest call that still forces a real gRPC round trip to
/// Qdrant. Any connectivity failure surfaces as the thrown exception, which the health service turns
/// into an unhealthy component with the exception message.
/// </summary>
public sealed class QdrantHealthProbe(QdrantClient client) : IQdrantHealthProbe
{
    public Task CheckAsync(CancellationToken cancellationToken) =>
        client.ListCollectionsAsync(cancellationToken);
}

/// <summary>
/// Fallback probe used when no Qdrant connection is configured and the in-memory
/// <c>LocalCodeChunkStore</c> is in play (tests, local dev without Docker). There is no external
/// service to reach, so the component is always healthy.
/// </summary>
public sealed class NotConfiguredQdrantHealthProbe : IQdrantHealthProbe
{
    public Task CheckAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
