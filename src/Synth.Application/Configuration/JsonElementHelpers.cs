using System.Text.Json;

namespace Synth.Application.Configuration;

/// <summary>
/// Shared helpers for reading a raw settings-PUT request body (a <see cref="JsonElement"/>) the same
/// way <see cref="Vcs.UpdateVcsSettingsCommandHandler"/> and
/// <see cref="Embeddings.UpdateEmbeddingSettingsCommandHandler"/> both need to: case-insensitive
/// property lookup (the wire contract is camelCase but callers shouldn't have to match it exactly) and
/// treating a JSON <c>null</c> the same as "no string value".
/// </summary>
internal static class JsonElementHelpers
{
    public static string? ToStringValueOrNull(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetString();

    public static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
