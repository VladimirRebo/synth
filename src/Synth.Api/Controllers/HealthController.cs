using Microsoft.AspNetCore.Mvc;
using Synth.Infrastructure.Health;

namespace Synth.Api.Controllers;

/// <summary>
/// Serves <c>GET /health</c>: Synth's readiness check. Unlike Aspire's own liveness endpoint at
/// <c>/alive</c> (mapped by <c>app.MapDefaultEndpoints()</c>, unrelated to this), this actually probes
/// the two live dependencies Synth needs at runtime — Qdrant and the configured embedding provider —
/// via <see cref="IHealthCheckService"/> (which caches briefly so polling doesn't hammer either).
/// </summary>
/// <remarks>
/// The route stays bare (no <c>/api</c> prefix, no class-level <c>[Route]</c>): the Vite dev proxy
/// strips <c>/api</c> before forwarding, matching every other endpoint in this app. This is the last
/// Minimal-API surface converted to a Controller (issue #82) — a thin status-code mapping over the
/// health service's report with no reusable business rule, so there's no Command/Query wrapper, same
/// judgment call as <c>SearchController</c> and <c>LogsController</c>. The health *service* itself lives
/// in <c>Synth.Infrastructure.Health</c>; this is just the route.
/// </remarks>
[ApiController]
public class HealthController : ControllerBase
{
    private readonly IHealthCheckService _health;

    public HealthController(IHealthCheckService health) => _health = health;

    /// <summary>
    /// <c>GET /health</c> — a fully healthy system returns 200 (so callers that only check the status
    /// code don't regress); if a component is down the body reports which and why, with status code 503.
    /// </summary>
    [HttpGet("/health")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var report = await _health.CheckAsync(cancellationToken);
        return report.Healthy
            ? Ok(report)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, report);
    }
}
