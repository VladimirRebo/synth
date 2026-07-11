using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Synth.Api.Health;
using Synth.Api.Mcp;

namespace Synth.Api.Tests;

// Proves SYNTH-43 (part 3): the `health_check` MCP tool returns the same HealthReport straight from
// IHealthCheckService — the same service GET /health reads — with no duplicated probing logic. The
// service itself is unit-tested in HealthCheckServiceTests; here a deterministic fake stands in.
public class HealthCheckMcpToolTests
{
    private sealed class FakeHealthCheckService(HealthReport report) : IHealthCheckService
    {
        public Task<HealthReport> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(report);
    }

    [Fact]
    public async Task Health_check_returns_the_service_report_verbatim()
    {
        var report = HealthReport.From(
            ComponentHealth.Unhealthy("the Qdrant probe failed: connection refused."),
            ComponentHealth.Ok);

        var result = await HealthCheckTool.HealthCheckAsync(new FakeHealthCheckService(report));

        Assert.Same(report, result);
        Assert.False(result.Healthy);
        Assert.Equal("degraded", result.Status);
        Assert.False(result.Qdrant.Healthy);
        Assert.Equal("the Qdrant probe failed: connection refused.", result.Qdrant.Error);
        Assert.True(result.Embedding.Healthy);
    }

    [Fact]
    public void Health_check_tool_is_registered_on_the_mcp_server()
    {
        using var factory = new WebApplicationFactory<Program>();

        var tools = factory.Services.GetServices<McpServerTool>();

        Assert.Contains(tools, tool => tool.ProtocolTool.Name == "health_check");
    }
}
