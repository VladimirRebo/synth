using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synth.Domain.Vcs;
using Synth.Infrastructure.Vcs;

namespace Synth.Api.Tests;

// Drives POST /webhooks/github over HTTP against GitHubWebhookController. Proves the HTTP-specific
// concerns — reading X-GitHub-Event/X-Hub-Signature-256 headers, mapping Unauthorized to 401 and
// everything else to 200 — over a real request/response round trip; the underlying signature-
// verification/branch-matching logic itself is unit-tested directly in
// ProcessGitHubWebhookCommandHandlerTests. Never dispatches a real reindex: every case here either
// fails auth or resolves to "Ignored" before reaching IndexRepositoryCommandHandler, so no real
// GitRepoService/embedding generator needs faking.
public class GitHubWebhookControllerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Secret = "test-secret";

    private readonly WebApplicationFactory<Program> _factory;

    public GitHubWebhookControllerTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient CreateClient(string? secret = Secret, IRepositoryRegistry? registry = null) =>
        _factory
            .WithWebHostBuilder(builder =>
            {
                if (secret is not null)
                    builder.UseSetting("Vcs:GitHub:WebhookSecret", secret);

                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IRepositoryRegistry>();
                    services.AddSingleton(registry ?? new InMemoryRepositoryRegistry());
                });
            })
            .CreateClient();

    [Fact]
    public async Task Missing_signature_header_returns_401()
    {
        var client = CreateClient();

        var response = await Post(client, "push", signature: null, PushBody());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_signature_returns_401()
    {
        var client = CreateClient();
        var body = PushBody();

        var response = await Post(client, "push", Sign(body, "wrong-secret"), body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task No_secret_configured_returns_401_even_with_a_wellformed_signature()
    {
        var client = CreateClient(secret: null);
        var body = PushBody();

        var response = await Post(client, "push", Sign(body, Secret), body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Valid_signature_for_an_unindexed_repository_returns_200_ignored()
    {
        var client = CreateClient();
        var body = PushBody();

        var response = await Post(client, "push", Sign(body, Secret), body);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(payload);
        Assert.Equal("Ignored", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Non_push_event_with_a_valid_signature_returns_200_ignored()
    {
        var client = CreateClient();
        var body = PushBody();

        var response = await Post(client, "issues", Sign(body, Secret), body);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(payload);
        Assert.Equal("Ignored", json.RootElement.GetProperty("status").GetString());
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string eventType, string? signature, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/github")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-GitHub-Event", eventType);
        if (signature is not null)
            request.Headers.Add("X-Hub-Signature-256", signature);

        return await client.SendAsync(request);
    }

    private static string PushBody() =>
        """
        {
            "ref": "refs/heads/main",
            "repository": {
                "clone_url": "https://github.com/owner/repo.git",
                "default_branch": "main"
            }
        }
        """;

    private static string Sign(string body, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexStringLower(hash);
    }
}
