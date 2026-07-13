using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synth.Application.Configuration;
using Synth.Domain.Configuration;

namespace Synth.Infrastructure.Configuration;

// Wires the config-store layer into the host: exposes the file-backed store
// through DI, feeds it into IConfiguration, and re-adds environment variables
// last so they always take precedence.
public static class ConfigStoreExtensions
{
    public static IHostApplicationBuilder AddSynthConfigStore(this IHostApplicationBuilder builder)
    {
        var store = CreateStore();

        builder.Services.AddSingleton(store);

        // Thread-safe read-merge-write of one section of the store document, used by the Settings
        // write APIs (e.g. PUT /settings/vcs) to persist a section and live-reload IConfiguration.
        builder.Services.AddSingleton<ConfigSectionUpdater>();
        // Same singleton behind the Application-layer port so command handlers there
        // (UpdateVcsSettingsCommandHandler) can depend on IConfigSectionUpdater without seeing this
        // concrete Infrastructure type; the endpoints that need the raw-document members keep using
        // ConfigSectionUpdater directly — mirrors how AddSynthVcs exposes GitRepoService as IGitRepoService.
        builder.Services.AddSingleton<IConfigSectionUpdater>(sp => sp.GetRequiredService<ConfigSectionUpdater>());

        // Layer 2: the config-store document, above appsettings.json.
        // ConfigurationManager implements IConfigurationBuilder explicitly, so add
        // the source through that interface.
        ((IConfigurationBuilder)builder.Configuration).Add(new ConfigStoreConfigurationSource(store));

        // Layer 3: environment variables, re-added last so they win over the store
        // (the standard __ -> : convention needs no extra code). IConfigurationManager
        // implements IConfigurationBuilder explicitly, so add through that interface.
        ((IConfigurationBuilder)builder.Configuration).AddEnvironmentVariables();

        return builder;
    }

    // Config is file/env only: distributing Synth must never force a client to run
    // a Mongo server just to hold configuration.
    private static IConfigStore CreateStore() => new FileConfigStore();
}
