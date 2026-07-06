using Microsoft.Extensions.Configuration;

namespace Synth.Api.Configuration;

// Bridges an IConfigStore into the .NET configuration system. Added to the
// configuration builder so the store's JSON document becomes a normal
// IConfiguration layer, sitting above appsettings.json and below environment
// variables.
public sealed class ConfigStoreConfigurationSource(IConfigStore store) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new ConfigStoreConfigurationProvider(store);
}
