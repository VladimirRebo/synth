using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synth.Api.Embeddings;
using Synth.Infrastructure.Configuration;
using Synth.Infrastructure.Embeddings;
using Synth.Application.Embeddings;
using Synth.Domain.Configuration;

namespace Synth.Api.Tests;

// Drives the Ollama model-picker endpoints over HTTP (SYNTH-50). The real Ollama HTTP call is faked via
// a stand-in IHttpClientFactory, so no live Ollama is contacted: the models proxy is fed a canned
// /api/tags body, and the pull is fed a canned newline-delimited-JSON /api/pull stream. An in-memory
// IConfigStore supplies Embedding:Ollama:Endpoint so the endpoint resolves without an Aspire connection.
public class OllamaModelEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string OllamaEndpoint = "http://ollama.test:11434";

    private readonly WebApplicationFactory<Program> _factory;

    public OllamaModelEndpointsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient CreateClient(FakeHttpClientFactory httpFactory, IOllamaPullTracker? tracker = null)
    {
        var store = new InMemoryConfigStore(
            $$"""{ "Embedding": { "Ollama": { "Endpoint": "{{OllamaEndpoint}}" } } }""");
        return _factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                    config.Add(new ConfigStoreConfigurationSource(store)));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IConfigStore>();
                    services.AddSingleton<IConfigStore>(store);
                    // Swap the real IHttpClientFactory so no live Ollama is hit.
                    services.RemoveAll<IHttpClientFactory>();
                    services.AddSingleton<IHttpClientFactory>(httpFactory);
                    if (tracker is not null)
                    {
                        services.RemoveAll<IOllamaPullTracker>();
                        services.AddSingleton(tracker);
                    }
                });
            })
            .CreateClient();
    }

    [Fact]
    public async Task Models_proxies_ollama_tags_and_returns_the_model_names()
    {
        var httpFactory = FakeHttpClientFactory.Responding((request, _) =>
        {
            Assert.Equal($"{OllamaEndpoint}/api/tags", request.RequestUri?.ToString());
            return Json("""
                { "models": [ { "name": "nomic-embed-text:latest" }, { "name": "llama3:8b" } ] }
                """);
        });

        var client = CreateClient(httpFactory);

        var models = await client.GetFromJsonAsync<List<string>>("/settings/embedding/ollama/models");

        Assert.Equal(["nomic-embed-text:latest", "llama3:8b"], models);
    }

    [Fact]
    public async Task Models_returns_502_when_ollama_is_unreachable()
    {
        var httpFactory = FakeHttpClientFactory.Throwing();

        var client = CreateClient(httpFactory);

        var response = await client.GetAsync("/settings/embedding/ollama/models");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Pull_accepts_immediately_and_status_reaches_Done_with_progress()
    {
        // A canned /api/pull stream: manifest -> a download line with byte counts -> success.
        var httpFactory = FakeHttpClientFactory.Responding((request, _) =>
        {
            Assert.Equal($"{OllamaEndpoint}/api/pull", request.RequestUri?.ToString());
            return Ndjson(
                """{ "status": "pulling manifest" }""",
                """{ "status": "downloading", "completed": 50, "total": 100 }""",
                """{ "status": "success" }""");
        });

        var client = CreateClient(httpFactory);

        var response = await client.PostAsJsonAsync(
            "/settings/embedding/ollama/pull", new { model = "nomic-embed-text" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var status = await WaitForTerminalStatusAsync(client);
        Assert.Equal("Done", status.GetProperty("state").GetString());
        Assert.Equal("nomic-embed-text", status.GetProperty("model").GetString());
        // The last progress line before success was the "success" status; the download line was surfaced
        // as a percentage while running.
        Assert.Null(status.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Pull_records_a_stream_error_as_Failed()
    {
        var httpFactory = FakeHttpClientFactory.Responding((_, _) =>
            Ndjson("""{ "error": "pull model manifest: file does not exist" }"""));

        var client = CreateClient(httpFactory);

        var response = await client.PostAsJsonAsync(
            "/settings/embedding/ollama/pull", new { model = "does-not-exist" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var status = await WaitForTerminalStatusAsync(client);
        Assert.Equal("Failed", status.GetProperty("state").GetString());
        Assert.Contains("file does not exist", status.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Pull_returns_409_when_one_is_already_running()
    {
        // A tracker pre-reserved as Running: the endpoint must reject a concurrent pull without dispatch.
        var tracker = new InMemoryOllamaPullTracker();
        tracker.TryStart("already-running");
        var httpFactory = FakeHttpClientFactory.Responding((_, _) => Ndjson("""{ "status": "success" }"""));

        var client = CreateClient(httpFactory, tracker);

        var response = await client.PostAsJsonAsync(
            "/settings/embedding/ollama/pull", new { model = "another" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Pull_rejects_a_blank_model()
    {
        var httpFactory = FakeHttpClientFactory.Responding((_, _) => Ndjson("""{ "status": "success" }"""));

        var client = CreateClient(httpFactory);

        var response = await client.PostAsJsonAsync("/settings/embedding/ollama/pull", new { model = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Polls GET .../pull/status until the pull leaves Running (Done or Failed), or times out. The tracker
    // is a process singleton, so the background pull's state is visible over HTTP.
    private static async Task<JsonElement> WaitForTerminalStatusAsync(HttpClient client, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var status = await client.GetFromJsonAsync<JsonElement>("/settings/embedding/ollama/pull/status");
            var state = status.GetProperty("state").GetString();
            if (state is "Done" or "Failed")
                return status;

            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Pull did not finish in time; last state was {state}.");

            await Task.Delay(25);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Ndjson(params string[] lines) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                string.Join('\n', lines.Select(l => JsonMinify(l))), Encoding.UTF8, "application/x-ndjson"),
        };

    // Collapse the multi-line raw string literals above into single physical lines so the endpoint's
    // line-by-line reader sees exactly one JSON object per line.
    private static string JsonMinify(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement);
    }

    // A deterministic stand-in for IHttpClientFactory: every CreateClient() returns an HttpClient over a
    // stub handler that either invokes a supplied responder or throws (network failure). No live Ollama.
    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly StubHandler _handler;

        private FakeHttpClientFactory(StubHandler handler) => _handler = handler;

        public static FakeHttpClientFactory Responding(
            Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) =>
            new(new StubHandler(responder));

        public static FakeHttpClientFactory Throwing() =>
            new(new StubHandler((_, _) => throw new HttpRequestException("simulated network failure")));

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);

        private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(responder(request, cancellationToken));
        }
    }
}
