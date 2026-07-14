using System.Text.Json.Serialization;

namespace Synth.Application.Health;

/// <summary>
/// The result of a <see cref="IHealthCheckService"/> probe: an overall verdict plus per-component
/// results for the two live dependencies Synth actually needs at runtime — the Qdrant vector store
/// and the configured embedding provider. Serialized as the <c>GET /health</c> JSON body, so the
/// property names are the wire contract (a healthy system still returns HTTP 200; an unhealthy one
/// returns 503 but carries this same shape so a caller can see which component is down).
/// </summary>
/// <param name="Status">Human-readable summary: <c>"ok"</c> when healthy, <c>"degraded"</c> otherwise.</param>
/// <param name="Healthy">True only when every component is healthy.</param>
/// <param name="Qdrant">Reachability of the Qdrant vector store.</param>
/// <param name="Embedding">Reachability of the configured embedding provider.</param>
public sealed record HealthReport(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("healthy")] bool Healthy,
    [property: JsonPropertyName("qdrant")] ComponentHealth Qdrant,
    [property: JsonPropertyName("embedding")] ComponentHealth Embedding)
{
    /// <summary>Combines per-component results into an overall report (healthy iff both are healthy).</summary>
    public static HealthReport From(ComponentHealth qdrant, ComponentHealth embedding)
    {
        var healthy = qdrant.Healthy && embedding.Healthy;
        return new HealthReport(healthy ? "ok" : "degraded", healthy, qdrant, embedding);
    }
}

/// <summary>
/// One dependency's health: whether it is reachable, and — when it is not — a human-readable reason
/// (null when healthy). Mirrors the "turn any failure into a clear string reason" shape the embedding
/// settings probe already uses.
/// </summary>
public sealed record ComponentHealth(
    [property: JsonPropertyName("healthy")] bool Healthy,
    [property: JsonPropertyName("error")] string? Error)
{
    public static readonly ComponentHealth Ok = new(true, null);

    public static ComponentHealth Unhealthy(string error) => new(false, error);
}
