using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synth.Api.Configuration;
using Synth.Domain.Configuration;

namespace Synth.Api.Tests;

// Drives GET/PUT /settings/vcs over HTTP. A single in-memory IConfigStore is shared between the
// endpoint (via DI) and an extra configuration layer, so the round trip is hermetic (no Mongo,
// Docker, or ~/.synth/config.json) AND the store's Changed event genuinely reloads
// IOptionsMonitor<VcsOptions> — the reload path the endpoint relies on.
public class VcsSettingsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public VcsSettingsEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private (HttpClient Client, InMemoryConfigStore Store) CreateClient(
        string? initialJson = null, FakeHttpClientFactory? probeFactory = null)
    {
        var store = new InMemoryConfigStore(initialJson);
        // Default: the token probe succeeds (200), so a PUT persists exactly as before SYNTH-37. Tests
        // that care about the probe pass a factory returning 401 / throwing to exercise the failure path.
        probeFactory ??= FakeHttpClientFactory.Responding(HttpStatusCode.OK);
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
                    // Swap the real IHttpClientFactory so the token probe never hits real GitHub/GitLab.
                    services.RemoveAll<IHttpClientFactory>();
                    services.AddSingleton<IHttpClientFactory>(probeFactory);
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

    [Fact]
    public async Task Valid_github_token_is_probed_and_persisted()
    {
        var probe = FakeHttpClientFactory.Responding(HttpStatusCode.OK);
        var (client, store) = CreateClient(probeFactory: probe);

        var put = await client.PutAsJsonAsync("/settings/vcs", new { github = new { token = "ghp_valid" } });
        put.EnsureSuccessStatusCode();

        // The token was actually probed against GitHub's public API before being saved...
        Assert.Equal(1, probe.SendCount);
        Assert.Equal("https://api.github.com/user", probe.LastRequestUri);
        Assert.Equal("Bearer ghp_valid", probe.LastAuthorization);
        Assert.NotNull(probe.LastUserAgent); // GitHub rejects requests without a User-Agent

        // ...and, the probe having succeeded, it is genuinely persisted.
        var get = await client.GetFromJsonAsync<JsonElement>("/settings/vcs");
        Assert.True(get.GetProperty("github").GetProperty("tokenSet").GetBoolean());
        using var stored = JsonDocument.Parse(store.Current!);
        Assert.Equal("ghp_valid",
            stored.RootElement.GetProperty("Vcs").GetProperty("GitHub").GetProperty("Token").GetString());
    }

    [Fact]
    public async Task Valid_gitlab_token_is_probed_with_private_token_header()
    {
        var probe = FakeHttpClientFactory.Responding(HttpStatusCode.OK);
        var (client, _) = CreateClient(probeFactory: probe);

        var put = await client.PutAsJsonAsync("/settings/vcs", new { gitlab = new { token = "glpat_valid" } });
        put.EnsureSuccessStatusCode();

        Assert.Equal(1, probe.SendCount);
        Assert.Equal("https://gitlab.com/api/v4/user", probe.LastRequestUri);
        Assert.Equal("glpat_valid", probe.LastPrivateToken);
    }

    [Fact]
    public async Task Invalid_token_probe_returns_400_and_leaves_config_unchanged()
    {
        var (client, store) = CreateClient(
            """{ "Vcs": { "GitHub": { "Token": "ghp_old" } } }""",
            FakeHttpClientFactory.Responding(HttpStatusCode.Unauthorized));
        var before = store.Current;

        var put = await client.PutAsJsonAsync("/settings/vcs", new { github = new { token = "ghp_bad" } });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);

        // Nothing was persisted: the stored document is byte-for-byte what it was before the failed PUT.
        Assert.Equal(before, store.Current);
        Assert.DoesNotContain("ghp_bad", store.Current!);

        // ...and a subsequent GET still shows the original token-set state, untouched.
        var get = await client.GetFromJsonAsync<JsonElement>("/settings/vcs");
        Assert.True(get.GetProperty("github").GetProperty("tokenSet").GetBoolean());
        using var stored = JsonDocument.Parse(store.Current!);
        Assert.Equal("ghp_old",
            stored.RootElement.GetProperty("Vcs").GetProperty("GitHub").GetProperty("Token").GetString());
    }

    [Fact]
    public async Task Network_failure_during_probe_is_rejected_without_persisting()
    {
        var (client, store) = CreateClient(probeFactory: FakeHttpClientFactory.Throwing());

        var put = await client.PutAsJsonAsync("/settings/vcs", new { gitlab = new { token = "glpat_unreachable" } });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Null(store.Current); // never saved
    }

    [Fact]
    public async Task Clearing_a_token_is_not_probed()
    {
        var probe = FakeHttpClientFactory.Responding(HttpStatusCode.OK);
        var (client, _) = CreateClient(
            """{ "Vcs": { "GitHub": { "Token": "ghp_old" } } }""", probe);

        var put = await client.PutAsJsonAsync("/settings/vcs", new { github = new { token = "" } });
        put.EnsureSuccessStatusCode();

        // Clearing a token needs no auth check — no probe was attempted.
        Assert.Equal(0, probe.SendCount);
        var get = await client.GetFromJsonAsync<JsonElement>("/settings/vcs");
        Assert.False(get.GetProperty("github").GetProperty("tokenSet").GetBoolean());
    }

    [Fact]
    public async Task Workspace_root_only_change_is_not_probed()
    {
        var probe = FakeHttpClientFactory.Responding(HttpStatusCode.OK);
        var (client, _) = CreateClient(probeFactory: probe);

        var put = await client.PutAsJsonAsync("/settings/vcs", new { workspaceRoot = "/new/root" });
        put.EnsureSuccessStatusCode();

        Assert.Equal(0, probe.SendCount); // a local path needs no probe
        var get = await client.GetFromJsonAsync<JsonElement>("/settings/vcs");
        Assert.Equal("/new/root", get.GetProperty("workspaceRoot").GetString());
    }

    // A deterministic stand-in for IHttpClientFactory: every CreateClient() returns an HttpClient over
    // a stub handler that either returns a fixed status code or throws (network failure), and records
    // what the endpoint sent so the probe request shape can be asserted. No real GitHub/GitLab call.
    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly RecordingHandler _handler;

        private FakeHttpClientFactory(RecordingHandler handler) => _handler = handler;

        public static FakeHttpClientFactory Responding(HttpStatusCode status) =>
            new(new RecordingHandler(status, throws: false));

        public static FakeHttpClientFactory Throwing() =>
            new(new RecordingHandler(HttpStatusCode.OK, throws: true));

        public int SendCount => _handler.SendCount;
        public string? LastRequestUri => _handler.LastRequestUri;
        public string? LastAuthorization => _handler.LastAuthorization;
        public string? LastUserAgent => _handler.LastUserAgent;
        public string? LastPrivateToken => _handler.LastPrivateToken;

        // The handler is intentionally shared and not disposed with the client, so recorded state
        // survives for post-request assertions.
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);

        private sealed class RecordingHandler(HttpStatusCode status, bool throws) : HttpMessageHandler
        {
            public int SendCount { get; private set; }
            public string? LastRequestUri { get; private set; }
            public string? LastAuthorization { get; private set; }
            public string? LastUserAgent { get; private set; }
            public string? LastPrivateToken { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                SendCount++;
                LastRequestUri = request.RequestUri?.ToString();
                LastAuthorization = request.Headers.Authorization?.ToString();
                LastUserAgent = request.Headers.UserAgent.Count == 0 ? null : request.Headers.UserAgent.ToString();
                LastPrivateToken = request.Headers.TryGetValues("PRIVATE-TOKEN", out var values)
                    ? string.Join(",", values)
                    : null;

                if (throws)
                    throw new HttpRequestException("simulated network failure");

                return Task.FromResult(new HttpResponseMessage(status));
            }
        }
    }
}
