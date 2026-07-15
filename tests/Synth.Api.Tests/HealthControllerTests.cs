using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synth.Application.Health;

namespace Synth.Api.Tests;

// Drives GET /health (HealthController) over HTTP. The reachability probes themselves are unit-tested
// against HealthCheckService directly (see HealthCheckServiceTests); here IHealthCheckService is swapped
// for a deterministic fake so the controller's own responsibility — status-code mapping and JSON shape —
// is exercised without a live Qdrant/Ollama.
public class HealthControllerTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public HealthControllerTests(TestApiFactory factory) => _factory = factory;

    private HttpClient CreateClient(HealthReport report)
    {
        return _factory
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHealthCheckService>();
                services.AddSingleton<IHealthCheckService>(new FakeHealthCheckService(report));
            }))
            .CreateClient();
    }

    [Fact]
    public async Task Healthy_system_returns_200_and_both_component_results()
    {
        var client = CreateClient(HealthReport.From(ComponentHealth.Ok, ComponentHealth.Ok));

        var response = await client.GetAsync("/health");

        // A fully healthy system must still return 200 so callers that only check the status code don't regress.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("healthy").GetBoolean());
        Assert.True(body.GetProperty("qdrant").GetProperty("healthy").GetBoolean());
        Assert.True(body.GetProperty("embedding").GetProperty("healthy").GetBoolean());
    }

    [Fact]
    public async Task Unhealthy_component_returns_503_and_reports_which_and_why()
    {
        var client = CreateClient(HealthReport.From(
            ComponentHealth.Unhealthy("the Qdrant probe failed: connection refused."),
            ComponentHealth.Ok));

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("degraded", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("healthy").GetBoolean());

        var qdrant = body.GetProperty("qdrant");
        Assert.False(qdrant.GetProperty("healthy").GetBoolean());
        Assert.Equal("the Qdrant probe failed: connection refused.", qdrant.GetProperty("error").GetString());

        // The still-healthy component is reported too (and carries no error).
        var embedding = body.GetProperty("embedding");
        Assert.True(embedding.GetProperty("healthy").GetBoolean());
        Assert.Null(embedding.GetProperty("error").GetString());
    }

    private sealed class FakeHealthCheckService(HealthReport report) : IHealthCheckService
    {
        public Task<HealthReport> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(report);
    }
}
