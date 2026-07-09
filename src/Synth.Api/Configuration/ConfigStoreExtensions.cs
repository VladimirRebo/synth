using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Synth.Api.Configuration;

// Wires the config-store layer into the host: selects Mongo vs File, exposes the
// store through DI, feeds it into IConfiguration, and re-adds environment
// variables last so they always take precedence.
public static class ConfigStoreExtensions
{
    // The Aspire connection-string name for Synth's Mongo database (see AppHost).
    private const string ConnectionName = "synthdata";

    public static WebApplicationBuilder AddSynthConfigStore(this WebApplicationBuilder builder)
    {
        var store = CreateStore(builder.Configuration);

        builder.Services.AddSingleton(store);

        // Thread-safe read-merge-write of one section of the store document, used by the Settings
        // write APIs (e.g. PUT /settings/vcs) to persist a section and live-reload IConfiguration.
        builder.Services.AddSingleton<ConfigSectionUpdater>();

        // Layer 2: the config-store document, above appsettings.json.
        // ConfigurationManager implements IConfigurationBuilder explicitly, so add
        // the source through that interface.
        ((IConfigurationBuilder)builder.Configuration).Add(new ConfigStoreConfigurationSource(store));

        // Layer 3: environment variables, re-added last so they win over the store
        // (the standard __ -> : convention needs no extra code).
        builder.Configuration.AddEnvironmentVariables();

        return builder;
    }

    // Mongo when a connection string is present, File otherwise — mirroring Sonar's
    // "ConnectionString present -> Mongo, else -> File" decision. Building a
    // MongoClient does not open a socket, so an absent/unreachable Mongo never
    // hard-fails here (LoadAsync degrades to an empty document).
    private static IConfigStore CreateStore(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
            return new FileConfigStore();

        var url = MongoUrl.Create(connectionString);
        var settings = MongoClientSettings.FromUrl(url);
        // Bound server selection so a missing Mongo fails fast instead of hanging
        // the initial (synchronous) configuration load.
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

        var database = new MongoClient(settings).GetDatabase(url.DatabaseName ?? "synth");
        return new MongoConfigStore(database);
    }
}
