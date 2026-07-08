using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Synth.Api.Configuration;
using Synth.Core.Embeddings;

namespace Synth.Api.Embeddings;

/// <summary>
/// Maps <c>GET</c>/<c>PUT /settings/embedding</c>: the read/write API over the <c>Embedding</c>
/// config section (<see cref="EmbeddingOptions"/>). Reads report whether the OpenAI API key is set
/// rather than echoing it (same masking as <c>SYNTH-20</c>'s VCS tokens); writes are partial (an absent
/// field is left unchanged) and persist through <see cref="ConfigSectionUpdater"/> so the change is
/// picked up live by <c>IOptionsMonitor&lt;EmbeddingOptions&gt;</c>.
/// <para>
/// Unlike VCS settings, an embedding config is cheaply provable, so a <c>PUT</c> is
/// <b>probe-before-persist</b>: a candidate generator built from the incoming config must produce one
/// real embedding for the fixed probe string before anything is saved. A probe failure (exception,
/// timeout, or an empty vector) is rejected with 400 and nothing is persisted — a saved broken provider
/// would otherwise poison every subsequent embedding request until fixed by hand.
/// </para>
/// </summary>
public static class EmbeddingSettingsEndpoints
{
    // Section keys mirror EmbeddingOptions' property names (config binding is case-insensitive, but we
    // write the canonical casing so the stored document reads the same as appsettings would).
    private const string ProviderKey = "Provider";
    private const string OllamaKey = "Ollama";
    private const string OpenAIKey = "OpenAI";
    private const string EndpointKey = "Endpoint";
    private const string ModelKey = "Model";
    private const string ApiKeyKey = "ApiKey";

    // Fixed probe text (matches Sonar's own dimension probe) and a short timeout so a hung/unreachable
    // provider fails the PUT quickly instead of blocking the request.
    private const string ProbeText = "dimension probe";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    public static IEndpointRouteBuilder MapEmbeddingSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // GET /settings/embedding — current effective EmbeddingOptions, API key masked to a flag.
        endpoints.MapGet("/settings/embedding",
            (IOptionsMonitor<EmbeddingOptions> options) => Results.Ok(Mask(options.CurrentValue)));

        // PUT /settings/embedding — probe the candidate config, then partial-update on success.
        endpoints.MapPut("/settings/embedding", async (
            JsonElement body,
            IEmbeddingGeneratorFactory factory,
            ConfigSectionUpdater updater,
            IOptionsMonitor<EmbeddingOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (body.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "Request body must be a JSON object." });

            // Merge the request over the current config so the probe sees exactly what a subsequent GET
            // would report — including the currently-stored API key when the caller omits it.
            var candidate = BuildCandidate(options.CurrentValue, body);

            var probeError = await ProbeAsync(factory, candidate, cancellationToken);
            if (probeError is not null)
                return Results.BadRequest(new { error = probeError });

            await updater.UpdateSectionAsync(EmbeddingOptions.SectionName, section =>
            {
                if (TryGetPropertyIgnoreCase(body, "provider", out var provider))
                    section[ProviderKey] = ToStringValueOrNull(provider);

                ApplyOllama(body, section);
                ApplyOpenAI(body, section);
            }, cancellationToken);

            // Built from IOptionsMonitor.CurrentValue after the save, so a correct masked shape here
            // proves the store's Changed event reloaded IConfiguration live (same guarantee as VCS).
            return Results.Ok(Mask(options.CurrentValue));
        });

        return endpoints;
    }

    // Generates one embedding for the probe text with the candidate generator under a short timeout.
    // Returns null on success, or a human-readable reason the config can't produce embeddings (so the
    // caller turns it into a 400). Never persists — this runs before the update.
    private static async Task<string?> ProbeAsync(
        IEmbeddingGeneratorFactory factory, EmbeddingOptions candidate, CancellationToken cancellationToken)
    {
        var generator = factory.Create(candidate);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            var result = await generator.GenerateAsync([ProbeText], cancellationToken: timeout.Token);
            if (result.Count == 0 || result[0].Vector.Length == 0)
                return "the embedding provider returned an empty vector for the probe.";

            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"the embedding probe timed out after {ProbeTimeout.TotalSeconds:0}s.";
        }
        catch (Exception ex)
        {
            return $"the embedding probe failed: {ex.Message}";
        }
        finally
        {
            generator.Dispose();
        }
    }

    // Builds the config the probe should validate: a copy of the current options with the request's
    // present fields applied on top (absent fields keep their current value — notably the stored API key).
    private static EmbeddingOptions BuildCandidate(EmbeddingOptions current, JsonElement body)
    {
        var candidate = new EmbeddingOptions
        {
            Provider = current.Provider,
            Ollama = { Endpoint = current.Ollama.Endpoint, Model = current.Ollama.Model },
            OpenAI = { ApiKey = current.OpenAI.ApiKey, Model = current.OpenAI.Model },
        };

        if (TryGetPropertyIgnoreCase(body, "provider", out var provider))
            candidate.Provider = ToStringValueOrNull(provider);

        if (TryGetPropertyIgnoreCase(body, "ollama", out var ollama) && ollama.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(ollama, "endpoint", out var endpoint))
                candidate.Ollama.Endpoint = ToStringValueOrNull(endpoint);
            if (TryGetPropertyIgnoreCase(ollama, "model", out var model))
                candidate.Ollama.Model = ToStringValueOrNull(model);
        }

        if (TryGetPropertyIgnoreCase(body, "openai", out var openai) && openai.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(openai, "apiKey", out var apiKey))
                candidate.OpenAI.ApiKey = ToStringValueOrNull(apiKey);
            if (TryGetPropertyIgnoreCase(openai, "model", out var model))
                candidate.OpenAI.Model = ToStringValueOrNull(model);
        }

        return candidate;
    }

    // Applies the Ollama block into the stored section — only fields actually present in the request,
    // so a partial PUT that omits (say) the model leaves the stored model intact.
    private static void ApplyOllama(JsonElement body, JsonObject section)
    {
        if (!TryGetPropertyIgnoreCase(body, "ollama", out var ollama) || ollama.ValueKind != JsonValueKind.Object)
            return;

        var target = GetOrAddObject(section, OllamaKey);
        if (TryGetPropertyIgnoreCase(ollama, "endpoint", out var endpoint))
            target[EndpointKey] = ToStringValueOrNull(endpoint);
        if (TryGetPropertyIgnoreCase(ollama, "model", out var model))
            target[ModelKey] = ToStringValueOrNull(model);
    }

    // Applies the OpenAI block. The API key follows SYNTH-20's convention: present -> set (empty string
    // clears), absent -> the stored key is left untouched (so a model-only update keeps the key).
    private static void ApplyOpenAI(JsonElement body, JsonObject section)
    {
        if (!TryGetPropertyIgnoreCase(body, "openai", out var openai) || openai.ValueKind != JsonValueKind.Object)
            return;

        var target = GetOrAddObject(section, OpenAIKey);
        if (TryGetPropertyIgnoreCase(openai, "apiKey", out var apiKey))
        {
            var value = ToStringValueOrNull(apiKey);
            target[ApiKeyKey] = string.IsNullOrEmpty(value) ? null : value;
        }

        if (TryGetPropertyIgnoreCase(openai, "model", out var model))
            target[ModelKey] = ToStringValueOrNull(model);
    }

    private static JsonObject GetOrAddObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
            return existing;

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static EmbeddingSettingsResponse Mask(EmbeddingOptions options) => new(
        string.IsNullOrWhiteSpace(options.Provider) ? null : options.Provider,
        new OllamaSettingsView(options.Ollama.Endpoint, options.Ollama.Model),
        new OpenAISettingsView(!string.IsNullOrEmpty(options.OpenAI.ApiKey), options.OpenAI.Model));

    private static string? ToStringValueOrNull(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetString();

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
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

/// <summary>Masked <c>Embedding</c> settings: the OpenAI API key is never echoed, only whether one is set.</summary>
public sealed record EmbeddingSettingsResponse(
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("ollama")] OllamaSettingsView Ollama,
    [property: JsonPropertyName("openai")] OpenAISettingsView OpenAI);

/// <summary>Ollama endpoint/model overrides as reported by GET (both may be null → Aspire fallback).</summary>
public sealed record OllamaSettingsView(
    [property: JsonPropertyName("endpoint")] string? Endpoint,
    [property: JsonPropertyName("model")] string? Model);

/// <summary>OpenAI settings without the secret: only whether a key is set, plus the model.</summary>
public sealed record OpenAISettingsView(
    [property: JsonPropertyName("apiKeySet")] bool ApiKeySet,
    [property: JsonPropertyName("model")] string? Model);
