using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Synth.Application.Configuration;
using Synth.Application.Vcs;
using Synth.Domain.Vcs;

namespace Synth.Application.Tests.Vcs;

// Proves SYNTH-68: the PUT /settings/vcs probe-before-persist body now lives in
// UpdateVcsSettingsCommandHandler, behaving identically to the old VcsSettingsEndpoints PUT handler.
// Runs offline against a recording IConfigSectionUpdater and a fake IHttpClientFactory (no real
// GitHub/GitLab, no config store), so the probe gate and the partial-update mutation are asserted
// directly on the handler — the unit-level counterpart to VcsSettingsControllerTests' HTTP round trip.
public class UpdateVcsSettingsCommandHandlerTests
{
    [Fact]
    public async Task Non_object_body_is_a_validation_error_and_persists_nothing()
    {
        var updater = new RecordingUpdater();
        var handler = CreateHandler(updater);

        var result = await handler.HandleAsync(new UpdateVcsSettingsCommand(Element("\"not an object\"")));

        Assert.False(result.Success);
        Assert.Equal("Request body must be a JSON object.", result.Error);
        Assert.False(updater.WasCalled);
    }

    [Fact]
    public async Task Valid_github_token_is_probed_then_persisted()
    {
        var probe = FakeHttpClientFactory.Responding(HttpStatusCode.OK);
        var updater = new RecordingUpdater();
        var handler = CreateHandler(updater, probe);

        var result = await handler.HandleAsync(new UpdateVcsSettingsCommand(
            Body(new { github = new { token = "ghp_valid" } })));

        Assert.True(result.Success);
        // Probed against GitHub's public API with a Bearer token + User-Agent before persisting...
        Assert.Equal(1, probe.SendCount);
        Assert.Equal("https://api.github.com/user", probe.LastRequestUri);
        Assert.Equal("Bearer ghp_valid", probe.LastAuthorization);
        Assert.NotNull(probe.LastUserAgent);
        // ...and, the probe having succeeded, the token is genuinely written to the section.
        Assert.True(updater.WasCalled);
        Assert.Equal("ghp_valid", updater.Section["GitHub"]!["Token"]!.GetValue<string>());
    }

    [Fact]
    public async Task Valid_gitlab_token_is_probed_with_private_token_header()
    {
        var probe = FakeHttpClientFactory.Responding(HttpStatusCode.OK);
        var handler = CreateHandler(new RecordingUpdater(), probe);

        var result = await handler.HandleAsync(new UpdateVcsSettingsCommand(
            Body(new { gitlab = new { token = "glpat_valid" } })));

        Assert.True(result.Success);
        Assert.Equal("https://gitlab.com/api/v4/user", probe.LastRequestUri);
        Assert.Equal("glpat_valid", probe.LastPrivateToken);
    }

    [Fact]
    public async Task Invalid_token_probe_is_a_validation_error_and_persists_nothing()
    {
        var updater = new RecordingUpdater(Seed("""{ "GitHub": { "Token": "ghp_old" } }"""));
        var handler = CreateHandler(updater, FakeHttpClientFactory.Responding(HttpStatusCode.Unauthorized));

        var result = await handler.HandleAsync(new UpdateVcsSettingsCommand(
            Body(new { github = new { token = "ghp_bad" } })));

        Assert.False(result.Success);
        Assert.Contains("GitHub", result.Error);
        // Nothing persisted: the section was never handed to the updater to mutate.
        Assert.False(updater.WasCalled);
        Assert.Equal("ghp_old", updater.Section["GitHub"]!["Token"]!.GetValue<string>());
    }

    [Fact]
    public async Task Network_failure_during_probe_is_a_validation_error()
    {
        var updater = new RecordingUpdater();
        var handler = CreateHandler(updater, FakeHttpClientFactory.Throwing());

        var result = await handler.HandleAsync(new UpdateVcsSettingsCommand(
            Body(new { gitlab = new { token = "glpat_unreachable" } })));

        Assert.False(result.Success);
        Assert.False(updater.WasCalled);
    }

    [Fact]
    public async Task Clearing_a_token_is_not_probed_and_stores_null()
    {
        var probe = FakeHttpClientFactory.Responding(HttpStatusCode.OK);
        var updater = new RecordingUpdater(Seed("""{ "GitHub": { "Token": "ghp_old" } }"""));
        var handler = CreateHandler(updater, probe);

        var result = await handler.HandleAsync(new UpdateVcsSettingsCommand(
            Body(new { github = new { token = "" } })));

        Assert.True(result.Success);
        Assert.Equal(0, probe.SendCount); // clearing needs no auth check
        Assert.True(updater.WasCalled);
        // The token was cleared to a JSON null, so the masked view reads it as not-set.
        var gitHub = updater.Section["GitHub"]!.AsObject();
        Assert.True(gitHub.ContainsKey("Token"));
        Assert.Null(gitHub["Token"]);
    }

    [Fact]
    public async Task Partial_update_leaves_the_other_provider_token_untouched()
    {
        var probe = FakeHttpClientFactory.Responding(HttpStatusCode.OK);
        var updater = new RecordingUpdater(Seed("""{ "GitLab": { "Token": "glpat_keep" } }"""));
        var handler = CreateHandler(updater, probe);

        var result = await handler.HandleAsync(new UpdateVcsSettingsCommand(
            Body(new { github = new { token = "ghp_new" } })));

        Assert.True(result.Success);
        Assert.Equal("ghp_new", updater.Section["GitHub"]!["Token"]!.GetValue<string>());
        // GitLab was not mentioned in the request, so its stored token survives.
        Assert.Equal("glpat_keep", updater.Section["GitLab"]!["Token"]!.GetValue<string>());
    }

    [Fact]
    public async Task Workspace_root_only_change_is_not_probed()
    {
        var probe = FakeHttpClientFactory.Responding(HttpStatusCode.OK);
        var updater = new RecordingUpdater();
        var handler = CreateHandler(updater, probe);

        var result = await handler.HandleAsync(new UpdateVcsSettingsCommand(
            Body(new { workspaceRoot = "/new/root" })));

        Assert.True(result.Success);
        Assert.Equal(0, probe.SendCount); // a local path needs no probe
        Assert.Equal("/new/root", updater.Section["WorkspaceRoot"]!.GetValue<string>());
    }

    [Fact]
    public async Task Success_returns_the_masked_current_options_never_echoing_the_token()
    {
        // The masked response is built from IOptionsMonitor.CurrentValue (post-reload in production).
        var options = new StaticOptionsMonitor<VcsOptions>(new VcsOptions
        {
            WorkspaceRoot = "/w",
            GitHub = new VcsOptions.ProviderAuth { Token = "ghp_secret" },
            GitLab = new VcsOptions.ProviderAuth { Token = null },
        });
        var handler = CreateHandler(new RecordingUpdater(), FakeHttpClientFactory.Responding(HttpStatusCode.OK), options);

        var result = await handler.HandleAsync(new UpdateVcsSettingsCommand(
            Body(new { workspaceRoot = "/w" })));

        Assert.True(result.Success);
        Assert.Equal("/w", result.Response!.WorkspaceRoot);
        Assert.True(result.Response.Github.TokenSet);
        Assert.False(result.Response.Gitlab.TokenSet);
        // The masked shape carries only flags — the raw token value is never present.
        Assert.DoesNotContain("ghp_secret", JsonSerializer.Serialize(result.Response));
    }

    private static UpdateVcsSettingsCommandHandler CreateHandler(
        IConfigSectionUpdater updater,
        FakeHttpClientFactory? probe = null,
        IOptionsMonitor<VcsOptions>? options = null) =>
        new(
            updater,
            options ?? new StaticOptionsMonitor<VcsOptions>(new VcsOptions()),
            probe ?? FakeHttpClientFactory.Responding(HttpStatusCode.OK));

    private static JsonElement Body(object value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonObject Seed(string json) => JsonNode.Parse(json)!.AsObject();

    // Records the mutation the handler applies to the config section, seeded with any pre-existing
    // state, so the partial-update / clear semantics can be asserted without a real IConfigStore.
    private sealed class RecordingUpdater(JsonObject? seed = null) : IConfigSectionUpdater
    {
        public bool WasCalled { get; private set; }
        public JsonObject Section { get; } = seed ?? new JsonObject();

        public Task UpdateSectionAsync(string sectionName, Action<JsonObject> mutate, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            mutate(Section);
            return Task.CompletedTask;
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    // A deterministic stand-in for IHttpClientFactory (mirrors the one in VcsSettingsControllerTests):
    // every CreateClient() returns an HttpClient over a stub handler that either returns a fixed status
    // code or throws (network failure), and records what the handler sent. No real GitHub/GitLab call.
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
