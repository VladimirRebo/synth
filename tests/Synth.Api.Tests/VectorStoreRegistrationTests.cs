using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Synth.Domain;
using Synth.Infrastructure.Storage;

namespace Synth.Api.Tests;

// Proves SYNTH-9 wiring: AddSynthVectorStore registers an ICodeChunkStore, selecting the
// Qdrant-backed store when a "qdrant" connection is supplied and the in-memory Local store
// otherwise — mirroring the Mongo/Ollama "connection present -> real backend, else fallback".
// The Qdrant client connects lazily, so both cases run without a live Qdrant/Docker.
public class VectorStoreRegistrationTests
{
    // Endpoint;Key shape Aspire injects for a referenced Qdrant resource.
    private const string QdrantConnectionString = "Endpoint=http://localhost:6334;Key=test-key";

    [Fact]
    public void ICodeChunkStore_falls_back_to_Local_without_a_qdrant_connection()
    {
        using var factory = new TestApiFactory();
        using var scope = factory.Services.CreateScope();

        var store = scope.ServiceProvider.GetService<ICodeChunkStore>();

        Assert.IsType<LocalCodeChunkStore>(store);
    }

    [Fact]
    public void ICodeChunkStore_is_Qdrant_backed_when_a_qdrant_connection_is_present()
    {
        using var factory = new TestApiFactory().WithWebHostBuilder(builder =>
            // Stand in for the connection string Aspire injects via service discovery.
            builder.UseSetting($"ConnectionStrings:{VectorStoreServiceExtensions.QdrantConnectionName}", QdrantConnectionString));
        using var scope = factory.Services.CreateScope();

        var store = scope.ServiceProvider.GetService<ICodeChunkStore>();

        Assert.IsType<QdrantCodeChunkStore>(store);
    }
}
