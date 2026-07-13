using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synth.Domain.Vcs;

namespace Synth.Infrastructure.Vcs;

/// <summary>
/// DI wiring for the VCS layer: binds <see cref="VcsOptions"/> from the <c>Vcs</c> config section
/// (through the layered IConfigStore/IOptionsMonitor machinery), registers <see cref="GitRepoService"/>
/// for cloning/fetching remote repos, and wires the <see cref="IRepositoryRegistry"/> to its
/// SQLite-backed implementation. Per issue #80 (slice SYNTH-62) the registry always uses the local
/// <c>~/.synth/synth.db</c> file — no Mongo-vs-fallback branching, mirroring how
/// <c>ConfigStoreExtensions.CreateStore</c> unconditionally returns <c>FileConfigStore</c> (SYNTH-53).
/// <see cref="InMemoryRepositoryRegistry"/> stays available for unit tests but is no longer wired
/// into production DI here.
/// </summary>
public static class VcsServiceExtensions
{
    public static IHostApplicationBuilder AddSynthVcs(this IHostApplicationBuilder builder)
    {
        builder.Services.Configure<VcsOptions>(builder.Configuration.GetSection(VcsOptions.SectionName));
        builder.Services.AddSingleton<GitRepoService>();
        builder.Services.AddSingleton<SqliteConnectionFactory>();
        builder.Services.AddSingleton<IRepositoryRegistry>(sp =>
            new SqliteRepositoryRegistry(sp.GetRequiredService<SqliteConnectionFactory>()));
        // IHttpClientFactory for VcsSettingsEndpoints' probe-before-persist token check (SYNTH-37).
        builder.Services.AddHttpClient();

        return builder;
    }
}
