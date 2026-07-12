using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Synth.Domain.Configuration;

namespace Synth.Infrastructure.Configuration;

// Loads the IConfigStore's JSON document, flattens it into colon-delimited
// configuration keys, and reloads whenever the store signals a change so that
// IOptionsMonitor<T> subscribers see updates without a restart.
public sealed class ConfigStoreConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly IConfigStore _store;

    public ConfigStoreConfigurationProvider(IConfigStore store)
    {
        _store = store;
        _store.Changed += OnStoreChanged;
    }

    public override void Load()
    {
        var json = _store.LoadAsync().GetAwaiter().GetResult();

        Data = string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : Flatten(json);
    }

    private void OnStoreChanged()
    {
        Load();
        OnReload();
    }

    // Flatten a JSON document into "section:subsection:key" pairs, mirroring how
    // the built-in JSON configuration provider maps objects and arrays.
    private static IDictionary<string, string?> Flatten(string json)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        Visit(document.RootElement, prefix: string.Empty, data);
        return data;
    }

    private static void Visit(JsonElement element, string prefix, IDictionary<string, string?> data)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Visit(property.Value, Combine(prefix, property.Name), data);
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    Visit(item, Combine(prefix, index++.ToString()), data);
                break;

            default:
                data[prefix] = element.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => element.GetString(),
                    _ => element.GetRawText(),
                };
                break;
        }
    }

    private static string Combine(string prefix, string key) =>
        string.IsNullOrEmpty(prefix) ? key : $"{prefix}{ConfigurationPath.KeyDelimiter}{key}";

    public void Dispose() => _store.Changed -= OnStoreChanged;
}
