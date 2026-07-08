using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synth.Api.Configuration;

namespace Synth.Api.Tests;

// Drives GET/PUT /settings/vcs over HTTP. A single in-memory IConfigStore is shared between the
// endpoint (via DI) and an extra configuration layer, so the round trip is hermetic (no Mongo,
// Docker, or ~/.synth/config.json) AND the store's Changed event genuinely reloads
// IOptionsMonitor<VcsOptions> — the reload path the endpoint relies on.
public class VcsSettingsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public VcsSettingsEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private (HttpClient Client, InMemoryConfigStore Store) CreateClient(string? initialJson = null)
    {
        var store = new InMemoryConfigStore(initialJson);
        var client = _factory
            .WithWebHostBuilder(builder =>
            {
                // Same store instance as an IConfiguration layer (so IOptionsMonitor sees reloads)...
                builder.ConfigureAppConfiguration((_, config) =>
                    config.Add(new ConfigStoreConfigurationSource(store)));
                // ...and as the DI IConfigStore the endpoint writes through.
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IConfigStore>();
                    services.AddSingleton<IConfigStore>(store);
                });
            })
            .CreateClient();
        return (client, store);
    }

    [Fact]
    public async Task Get_reports_masked_status_and_never_echoes_tokens()
    {
        var (client, _) = CreateClient(
            """{ "Vcs": { "WorkspaceRoot": "/w", "GitHub": { "Token": "ghp_secret" }, "GitLab": { "Token": null } } }""");

        var response = await client.GetAsync("/settings/vcs");
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("ghp_secret", payload);
        using var json = JsonDocument.Parse(payload);
        Assert.Equal("/w", json.RootElement.GetProperty("workspaceRoot").GetString());
        Assert.True(json.RootElement.GetProperty("github").GetProperty("tokenSet").GetBoolean());
        Assert.False(json.RootElement.GetProperty("gitlab").GetProperty("tokenSet").GetBoolean());
    }

    [Fact]
    public async Task Put_then_get_round_trips_the_masked_shape()
    {
        var (client, _) = CreateClient();

        var put = await client.PutAsJsonAsync("/settings/vcs", new
        {
            workspaceRoot = "/work",
            github = new { token = "ghp_live" },
        });
        put.EnsureSuccessStatusCode();
        var putBody = await put.Content.ReadAsStringAsync();
        Assert.DoesNotContain("ghp_live", putBody);

        var get = await client.GetFromJsonAsync<JsonElement>("/settings/vcs");
        Assert.Equal("/work", get.GetProperty("workspaceRoot").GetString());
        Assert.True(get.GetProperty("github").GetProperty("tokenSet").GetBoolean());
        Assert.False(get.GetProperty("gitlab").GetProperty("tokenSet").GetBoolean());
    }

    [Fact]
    public async Task Partial_put_leaves_the_other_provider_token_untouched()
    {
        var (client, store) = CreateClient(
            """{ "Vcs": { "GitLab": { "Token": "glpat_keep" } } }""");

        // Update only the GitHub token; GitLab is not mentioned and must survive.
        var put = await client.PutAsJsonAsync("/settings/vcs", new { github = new { token = "ghp_new" } });
        put.EnsureSuccessStatusCode();

        var get = await client.GetFromJsonAsync<JsonElement>("/settings/vcs");
        Assert.True(get.GetProperty("github").GetProperty("tokenSet").GetBoolean());
        Assert.True(get.GetProperty("gitlab").GetProperty("tokenSet").GetBoolean());

        // The stored GitLab token is genuinely still there (not just reported set).
        using var stored = JsonDocument.Parse(store.Current!);
        Assert.Equal("glpat_keep",
            stored.RootElement.GetProperty("Vcs").GetProperty("GitLab").GetProperty("Token").GetString());
    }

    [Fact]
    public async Task Empty_string_token_clears_it()
    {
        var (client, _) = CreateClient(
            """{ "Vcs": { "GitHub": { "Token": "ghp_old" } } }""");

        var put = await client.PutAsJsonAsync("/settings/vcs", new { github = new { token = "" } });
        put.EnsureSuccessStatusCode();

        var get = await client.GetFromJsonAsync<JsonElement>("/settings/vcs");
        Assert.False(get.GetProperty("github").GetProperty("tokenSet").GetBoolean());
    }

    [Fact]
    public async Task Put_is_observable_through_options_monitor_without_restart()
    {
        var (client, _) = CreateClient();

        var put = await client.PutAsJsonAsync("/settings/vcs", new { gitlab = new { token = "glpat_live" } });
        put.EnsureSuccessStatusCode();

        // The endpoint builds its response from IOptionsMonitor<VcsOptions>.CurrentValue *after* the
        // save, so a set flag here proves the store's Changed event reloaded IConfiguration and the
        // new token propagated into VcsOptions live — not just that the stored JSON changed.
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("gitlab").GetProperty("tokenSet").GetBoolean());
    }
}
