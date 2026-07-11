using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synth.Core;
using Synth.Domain;

namespace Synth.Api.Storage;

/// <summary>
/// DI wiring for Synth's vector store. Registers an <see cref="ICodeChunkStore"/> backed by
/// Qdrant when a connection is present (via the Aspire Qdrant client integration), and falls
/// back to the in-memory <see cref="LocalCodeChunkStore"/> otherwise — mirroring the
/// "connection string present → real backend, else fallback" decision used for Mongo and Ollama.
/// The fallback is what tests and local dev-without-Docker run on.
/// </summary>
public static class VectorStoreServiceExtensions
{
    // Aspire connection name for Qdrant; must match the resource registered in the AppHost.
    public const string QdrantConnectionName = "qdrant";

    public static IHostApplicationBuilder AddSynthVectorStore(this IHostApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString(QdrantConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // No Qdrant configured: brute-force in-memory store. No Docker/network needed.
            builder.Services.AddSingleton<ICodeChunkStore, LocalCodeChunkStore>();
            return builder;
        }

        // Registers a QdrantClient from the Aspire-supplied endpoint + API key. The gRPC
        // channel connects lazily, so registration opens no socket.
        builder.AddQdrantClient(QdrantConnectionName);
        builder.Services.AddSingleton<ICodeChunkStore, QdrantCodeChunkStore>();
        return builder;
    }
}
