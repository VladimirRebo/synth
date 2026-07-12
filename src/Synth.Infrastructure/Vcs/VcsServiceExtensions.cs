using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Synth.Domain.Vcs;

namespace Synth.Infrastructure.Vcs;

/// <summary>
/// DI wiring for the VCS layer: binds <see cref="VcsOptions"/> from the <c>Vcs</c> config section
/// (through the layered IConfigStore/IOptionsMonitor machinery), registers <see cref="GitRepoService"/>
/// for cloning/fetching remote repos, and picks the <see cref="IRepositoryRegistry"/> implementation —
/// Mongo when a connection string is present, an in-memory fallback otherwise (mirroring
/// <c>AddSynthConfigStore</c>'s Mongo-vs-File selection).
/// </summary>
public static class VcsServiceExtensions
{
    // The Aspire connection-string name for Synth's Mongo database (see AppHost / ConfigStoreExtensions).
    private const string ConnectionName = "synthdata";

    public static IHostApplicationBuilder AddSynthVcs(this IHostApplicationBuilder builder)
    {
        builder.Services.Configure<VcsOptions>(builder.Configuration.GetSection(VcsOptions.SectionName));
        builder.Services.AddSingleton<GitRepoService>();
        builder.Services.AddSingleton(CreateRegistry(builder.Configuration));
        // IHttpClientFactory for VcsSettingsEndpoints' probe-before-persist token check (SYNTH-37).
        builder.Services.AddHttpClient();

        return builder;
    }

    // Mongo when a connection string is present, in-memory otherwise. Building a MongoClient does
    // not open a socket, and the Mongo registry degrades gracefully on read/write, so an
    // absent/unreachable Mongo never hard-fails here.
    private static IRepositoryRegistry CreateRegistry(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
            return new InMemoryRepositoryRegistry();

        var url = MongoUrl.Create(connectionString);
        var settings = MongoClientSettings.FromUrl(url);
        // Bound server selection so a missing Mongo fails fast (and is then swallowed) instead of hanging.
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

        var database = new MongoClient(settings).GetDatabase(url.DatabaseName ?? "synth");
        return new MongoRepositoryRegistry(database);
    }
}
