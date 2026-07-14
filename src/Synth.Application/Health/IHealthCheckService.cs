namespace Synth.Application.Health;

/// <summary>
/// Application-layer port for probing Synth's live dependencies, so <c>HealthController</c> and
/// <c>HealthCheckTool</c> can depend on the capability without referencing the concrete
/// <c>HealthCheckService</c> in Synth.Infrastructure — Infrastructure references Application, not
/// the other way round, so the port lives here and the implementation realizes it, exactly as
/// <see cref="Synth.Application.Vcs.IGitRepoService"/> already does for its service.
/// </summary>
public interface IHealthCheckService
{
    /// <summary>Probes Qdrant and the embedding provider (result cached briefly) and reports per-component status.</summary>
    Task<HealthReport> CheckAsync(CancellationToken cancellationToken);
}
