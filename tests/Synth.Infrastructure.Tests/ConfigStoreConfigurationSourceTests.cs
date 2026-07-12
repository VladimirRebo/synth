using Microsoft.Extensions.Configuration;
using Synth.Infrastructure.Configuration;
using Synth.Domain.Configuration;

namespace Synth.Infrastructure.Tests;

// Verifies the configuration glue: a JSON document from any IConfigStore is
// flattened into IConfiguration keys, environment variables override it, and a
// store change triggers a live reload. Uses an in-memory store, so no
// Mongo/Docker/file access is required.
public class ConfigStoreConfigurationSourceTests
{
    private sealed class InMemoryConfigStore(string? json) : IConfigStore
    {
        private string? _json = json;

        public Task<string?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_json);

        public Task SaveAsync(string json, CancellationToken cancellationToken = default)
        {
            _json = json;
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public event Action? Changed;
    }

    [Fact]
    public void Flattens_nested_json_into_colon_delimited_keys()
    {
        const string json = """
            {
              "Logging": { "LogLevel": { "Default": "Information" } },
              "Feature": { "Enabled": true },
              "Hosts": [ "a", "b" ]
            }
            """;

        var config = new ConfigurationBuilder()
            .Add(new ConfigStoreConfigurationSource(new InMemoryConfigStore(json)))
            .Build();

        Assert.Equal("Information", config["Logging:LogLevel:Default"]);
        Assert.Equal("true", config["Feature:Enabled"]);
        Assert.Equal("a", config["Hosts:0"]);
        Assert.Equal("b", config["Hosts:1"]);
    }

    [Fact]
    public void Environment_variables_override_the_config_store()
    {
        const string envKey = "SynthTest__Value";
        const string json = """{ "SynthTest": { "Value": "fromStore" } }""";
        Environment.SetEnvironmentVariable(envKey, "fromEnv");
        try
        {
            var config = new ConfigurationBuilder()
                .Add(new ConfigStoreConfigurationSource(new InMemoryConfigStore(json)))
                .AddEnvironmentVariables()
                .Build();

            Assert.Equal("fromEnv", config["SynthTest:Value"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, null);
        }
    }

    [Fact]
    public async Task Reloads_when_the_store_changes()
    {
        var store = new InMemoryConfigStore("""{ "Key": "before" }""");
        var config = new ConfigurationBuilder()
            .Add(new ConfigStoreConfigurationSource(store))
            .Build();
        Assert.Equal("before", config["Key"]);

        await store.SaveAsync("""{ "Key": "after" }""");

        Assert.Equal("after", config["Key"]);
    }
}
