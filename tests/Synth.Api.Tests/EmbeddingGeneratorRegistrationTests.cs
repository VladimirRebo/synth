using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Synth.Api.Tests;

// Proves SYNTH-8 wiring: the Aspire Community Toolkit Ollama client integration
// registers an IEmbeddingGenerator<string, Embedding<float>> in DI, resolved from
// the Aspire-supplied connection string (no hardcoded value in the app). The Ollama
// client is created lazily and does not open a socket, so this runs without a live
// Ollama server/Docker.
public class EmbeddingGeneratorRegistrationTests : IClassFixture<TestApiFactory>
{
    // Endpoint;Model shape Aspire injects for a referenced Ollama model resource.
    private const string TestConnectionString = "Endpoint=http://localhost:11434;Model=nomic-embed-text";

    private readonly WebApplicationFactory<Program> _factory;

    public EmbeddingGeneratorRegistrationTests(TestApiFactory factory) =>
        _factory = factory.WithWebHostBuilder(builder =>
            // Stand in for the connection string Aspire injects via service discovery.
            builder.UseSetting("ConnectionStrings:embeddings", TestConnectionString));

    [Fact]
    public void IEmbeddingGenerator_is_registered()
    {
        using var scope = _factory.Services.CreateScope();

        var generator = scope.ServiceProvider
            .GetService<IEmbeddingGenerator<string, Embedding<float>>>();

        Assert.NotNull(generator);
    }
}
