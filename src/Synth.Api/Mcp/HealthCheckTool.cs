using System.ComponentModel;
using ModelContextProtocol.Server;
using Synth.Api.Health;

namespace Synth.Api.Mcp;

/// <summary>
/// Transport-agnostic MCP tool that reports Synth's live-dependency health — the MCP equivalent of
/// <c>GET /health</c> (SYNTH-35, part of issue #44). A thin wrapper: it returns the same
/// <see cref="HealthReport"/> (overall verdict plus per-component Qdrant/embedding status) straight
/// from <see cref="IHealthCheckService.CheckAsync"/>, the same service the REST endpoint reads.
/// Registered via <c>AddMcpServer().WithTools&lt;HealthCheckTool&gt;()</c> over both the HTTP and stdio
/// transports; over stdio the service falls back to the always-healthy Qdrant probe when no live
/// Qdrant is configured, matching SYNTH-35's own "not configured" pattern.
/// </summary>
[McpServerToolType]
public sealed class HealthCheckTool
{
    /// <summary>
    /// Probes Qdrant and the configured embedding provider (result cached briefly by the service) and
    /// returns the per-component report. <paramref name="health"/> is injected from DI per invocation.
    /// </summary>
    [McpServerTool(Name = "health_check")]
    [Description(
        "Check whether Synth's live dependencies are reachable — the Qdrant vector store and the " +
        "configured embedding provider. Returns an overall verdict plus a per-component result " +
        "(healthy plus, when unhealthy, a human-readable reason).")]
    public static async Task<HealthReport> HealthCheckAsync(
        IHealthCheckService health,
        CancellationToken cancellationToken = default) =>
        await health.CheckAsync(cancellationToken);
}
