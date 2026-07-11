using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synth.Api.Configuration;

namespace Synth.Api.Tests;

// Drives GET/PUT /settings/raw over HTTP. As with the section-settings tests, one in-memory
// IConfigStore backs both the endpoint's writes and a configuration layer, so the round trip is
// hermetic (no Mongo/Docker/~/.synth/config.json) AND the store's Changed event genuinely reloads
// IOptionsMonitor<VcsOptions>/<EmbeddingOptions> — the reload path the endpoint shares with them.
//
// Unlike every other Settings endpoint, /settings/raw is deliberately UNMASKED: these tests assert
// secrets round-trip verbatim, guarding against someone "fixing" this into masking by copy-paste.
public class RawSettingsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RawSettingsEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private (HttpClient Client, InMemoryConfigStore Store) CreateClient(string? initialJson = null)
    {
        var store = new InMemoryConfigStore(initialJson);
        var client = _factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                    config.Add(new ConfigStoreConfigurationSource(store)));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IConfigStore>();
                    services.AddSingleton<IConfigStore>(store);
                });
            })
            .CreateClient();
        return (client, store);
    }

    private static StringContent Raw(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Get_returns_empty_object_when_nothing_is_stored()
    {
        var (client, _) = CreateClient();

        var response = await client.GetAsync("/settings/raw");
        response.EnsureSuccessStatusCode();

        Assert.Equal("{}", (await response.Content.ReadAsStringAsync()).Trim());
    }

    [Fact]
    public async Task Put_then_get_round_trips_the_whole_document_unmasked()
    {
        var (client, _) = CreateClient();

        // A compact document with secret-shaped values in both sections.
        const string document =
            """{"Vcs":{"GitHub":{"Token":"ghp_secret_raw"}},"Embedding":{"OpenAI":{"ApiKey":"sk-secret-raw"}}}""";

        var put = await client.PutAsync("/settings/raw", Raw(document));
        put.EnsureSuccessStatusCode();

        // PUT echoes the persisted document with secrets included — this is the whole point of the endpoint.
        var putBody = await put.Content.ReadAsStringAsync();
        Assert.Contains("ghp_secret_raw", putBody);
        Assert.Contains("sk-secret-raw", putBody);

        // A subsequent GET returns exactly what was written, unmasked (opposite of the section endpoints).
        var get = await client.GetAsync("/settings/raw");
        get.EnsureSuccessStatusCode();
        var getBody = await get.Content.ReadAsStringAsync();
        Assert.Equal(document, getBody);
        Assert.Contains("ghp_secret_raw", getBody);
        Assert.Contains("sk-secret-raw", getBody);
    }

    [Fact]
    public async Task Malformed_json_body_is_rejected_with_400_and_nothing_is_persisted()
    {
        var (client, store) = CreateClient();

        var response = await client.PutAsync("/settings/raw", Raw("{ this is not valid json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(store.Current); // malformed input never reached the store
    }

    [Fact]
    public async Task Non_object_json_body_is_rejected_with_400_and_nothing_is_persisted()
    {
        var (client, store) = CreateClient();

        // Well-formed JSON, but an array — the config provider needs a top-level object.
        var response = await client.PutAsync("/settings/raw", Raw("[1, 2, 3]"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(store.Current);
    }

    [Fact]
    public async Task Put_with_only_known_sections_persists_without_a_warning_header()
    {
        var (client, store) = CreateClient();

        const string document =
            """{"Vcs":{"GitHub":{"Token":"ghp_ok"}},"Embedding":{"Provider":"OpenAI"}}""";

        var put = await client.PutAsync("/settings/raw", Raw(document));
        put.EnsureSuccessStatusCode();

        Assert.False(put.Headers.Contains(RawSettingsEndpoints.WarningsHeader));
        Assert.Equal(document, store.Current); // still persisted verbatim
    }

    [Fact]
    public async Task Put_with_unknown_top_level_key_still_persists_but_warns_identifying_the_key()
    {
        var (client, store) = CreateClient();

        // "Typo" matches no section this app reads; "Vcs" is known.
        const string document =
            """{"Vcs":{"GitHub":{"Token":"ghp_ok"}},"Typo":{"Foo":"bar"}}""";

        var put = await client.PutAsync("/settings/raw", Raw(document));
        put.EnsureSuccessStatusCode();

        // The write is NOT blocked — the unknown key is stored exactly as sent.
        Assert.Equal(document, store.Current);

        // ...but a non-blocking warning surfaces, naming the offending key.
        Assert.True(put.Headers.Contains(RawSettingsEndpoints.WarningsHeader));
        var warnings = put.Headers.GetValues(RawSettingsEndpoints.WarningsHeader).Single();
        Assert.Contains("Typo", warnings);
        Assert.DoesNotContain("Vcs", warnings); // the known section is not flagged
    }

    [Fact]
    public async Task Put_matches_known_section_names_case_insensitively()
    {
        var (client, _) = CreateClient();

        // Lowercase "vcs"/"embedding" bind case-insensitively, so they are not a typo — no warning.
        const string document = """{"vcs":{"GitHub":{"Token":"ghp_ok"}},"embedding":{"Provider":"OpenAI"}}""";

        var put = await client.PutAsync("/settings/raw", Raw(document));
        put.EnsureSuccessStatusCode();

        Assert.False(put.Headers.Contains(RawSettingsEndpoints.WarningsHeader));
    }

    [Fact]
    public async Task Put_is_observable_through_the_options_monitors_like_the_section_endpoints()
    {
        var (client, _) = CreateClient();

        const string document =
            """
            {
              "Vcs": { "GitHub": { "Token": "ghp_raw_live" } },
              "Embedding": { "Provider": "OpenAI", "OpenAI": { "ApiKey": "sk-raw-live", "Model": "text-embedding-3-small" } }
            }
            """;

        var put = await client.PutAsync("/settings/raw", Raw(document));
        put.EnsureSuccessStatusCode();

        // The section GET endpoints report from IOptionsMonitor<VcsOptions>/<EmbeddingOptions>.CurrentValue,
        // so a set flag / value here proves the raw write reloaded IConfiguration through the same path —
        // it isn't a separate, disconnected storage lane.
        var vcs = await client.GetFromJsonAsync<JsonElement>("/settings/vcs");
        Assert.True(vcs.GetProperty("github").GetProperty("tokenSet").GetBoolean());

        var embedding = await client.GetFromJsonAsync<JsonElement>("/settings/embedding");
        Assert.Equal("OpenAI", embedding.GetProperty("provider").GetString());
        Assert.True(embedding.GetProperty("openai").GetProperty("apiKeySet").GetBoolean());
    }
}
