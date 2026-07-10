using Microsoft.Extensions.DependencyInjection;
using Qdrant.Client;

namespace Synth.Api.Health;

/// <summary>
/// DI wiring for Synth's <c>GET /health</c> reachability checks. Registers the
/// <see cref="IHealthCheckService"/> plus the Qdrant probe seam it depends on: the real
/// <see cref="QdrantHealthProbe"/> when a <see cref="QdrantClient"/> is in DI (Qdrant configured), or the
/// always-healthy <see cref="NotConfiguredQdrantHealthProbe"/> when the in-memory store is in use —
/// mirroring the same "connection present → real backend, else fallback" decision the vector store makes.
/// </summary>
public static class HealthServiceExtensions
{
    public static IServiceCollection AddSynthHealthChecks(this IServiceCollection services)
    {
        // Resolve QdrantClient lazily: it's only registered when a Qdrant connection is configured, so
        // GetService (not GetRequiredService) lets the in-memory fallback report healthy instead of throwing.
        services.AddSingleton<IQdrantHealthProbe>(sp =>
        {
            var client = sp.GetService<QdrantClient>();
            return client is not null ? new QdrantHealthProbe(client) : new NotConfiguredQdrantHealthProbe();
        });

        services.AddSingleton<IHealthCheckService, HealthCheckService>();
        return services;
    }
}
