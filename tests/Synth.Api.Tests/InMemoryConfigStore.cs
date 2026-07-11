using Synth.Api.Configuration;

namespace Synth.Api.Tests;

// A minimal IConfigStore that keeps the JSON document in memory and raises Changed on save,
// so tests can exercise the config-layering + live-reload path without touching Mongo, Docker,
// or the real ~/.synth/config.json file.
public sealed class InMemoryConfigStore(string? json = null) : IConfigStore
{
    private string? _json = json;

    public event Action? Changed;

    public Task<string?> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_json);

    public Task SaveAsync(string json, CancellationToken cancellationToken = default)
    {
        _json = json;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public string? Current => _json;
}
