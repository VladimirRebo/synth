using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Synth.Core.Graph;

namespace Synth.Api.Graph;

/// <summary>
/// DI wiring for the call-graph storage layer: picks the <see cref="ICodeGraphStore"/> implementation
/// — Mongo when the <c>synthdata</c> connection string is present, an in-memory fallback otherwise
/// (mirroring <c>VcsServiceExtensions.CreateRegistry</c>). Registration only — nothing consumes the
/// store yet (extraction is SYNTH-26, query tools are SYNTH-27).
/// </summary>
public static class CodeGraphServiceExtensions
{
    // The Aspire connection-string name for Synth's Mongo database (see AppHost / ConfigStoreExtensions).
    private const string ConnectionName = "synthdata";

    public static IHostApplicationBuilder AddSynthCodeGraph(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton(CreateStore(builder.Configuration));
        return builder;
    }

    // Mongo when a connection string is present, in-memory otherwise. Building a MongoClient does
    // not open a socket, and the Mongo store degrades gracefully on read/write, so an
    // absent/unreachable Mongo never hard-fails here.
    private static ICodeGraphStore CreateStore(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
            return new InMemoryCodeGraphStore();

        var url = MongoUrl.Create(connectionString);
        var settings = MongoClientSettings.FromUrl(url);
        // Bound server selection so a missing Mongo fails fast (and is then swallowed) instead of hanging.
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

        var database = new MongoClient(settings).GetDatabase(url.DatabaseName ?? "synth");
        return new MongoCodeGraphStore(database);
    }
}
