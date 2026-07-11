using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Synth.Domain.Logging;

namespace Synth.Api.Logging;

/// <summary>
/// DI wiring for the log-persistence layer: registers the <see cref="LogEntryStoreSink"/> (already
/// constructed and attached to Serilog in <c>Program.cs</c>), picks the <see cref="ILogEntryStore"/>
/// implementation — Mongo when the <c>synthdata</c> connection string is present, an in-memory
/// fallback otherwise (mirroring <c>VcsServiceExtensions.CreateRegistry</c> and
/// <c>CodeGraphServiceExtensions</c>) — and starts the <see cref="LogEntryStoreWriter"/> background
/// drain that moves entries from the sink's channel into that store.
/// </summary>
public static class LoggingServiceExtensions
{
    // The Aspire connection-string name for Synth's Mongo database (see AppHost / ConfigStoreExtensions).
    private const string ConnectionName = "synthdata";

    /// <param name="sink">The sink already wired into Serilog in Program.cs; shared here as the DI
    /// singleton the background writer drains.</param>
    public static IHostApplicationBuilder AddSynthLogging(this IHostApplicationBuilder builder, LogEntryStoreSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        builder.Services.AddSingleton(sink);
        builder.Services.AddSingleton(CreateStore(builder.Configuration));
        builder.Services.AddHostedService<LogEntryStoreWriter>();

        return builder;
    }

    // Mongo when a connection string is present, in-memory otherwise. Building a MongoClient does not
    // open a socket, and MongoLogEntryStore degrades gracefully on read/write, so an absent/unreachable
    // Mongo never hard-fails here.
    private static ILogEntryStore CreateStore(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
            return new InMemoryLogEntryStore();

        var url = MongoUrl.Create(connectionString);
        var settings = MongoClientSettings.FromUrl(url);
        // Bound server selection so a missing Mongo fails fast (and is then swallowed) instead of hanging.
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

        var database = new MongoClient(settings).GetDatabase(url.DatabaseName ?? "synth");
        return new MongoLogEntryStore(database);
    }
}
