using System.Data.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using Synth.Core.Embeddings;

namespace Synth.Api.Embeddings;

/// <summary>
/// An <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> (TInput = <c>string</c>,
/// TEmbedding = <c>Embedding&lt;float&gt;</c>) whose backing provider is chosen at runtime from
/// <see cref="EmbeddingOptions"/> and rebuilt whenever that config changes — no Aspire-host restart
/// needed to switch between Ollama and OpenAI or change the model. Mirrors Sonar's
/// <c>ConfigurableEmbeddingGenerator</c>.
/// <para>
/// The inner generator is (re)built lazily on the first call after a config change, guarded by a
/// snapshot key so an unchanged config reuses the existing instance, and by double-checked locking so
/// concurrent callers don't each rebuild. When the config is incomplete (e.g. OpenAI selected with no
/// API key) a <see cref="NotConfiguredEmbeddingGenerator"/> sentinel is returned instead, so nothing
/// throws at construction/DI-resolution time — the app still starts, and the error only surfaces when
/// an embedding is actually requested.
/// </para>
/// </summary>
public sealed class ConfigurableEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    // Separator for the config fingerprint: a control char that can't appear in an endpoint/model/key,
    // so distinct field combinations never collide into the same key.
    private const char KeySeparator = '\u0001';

    // Default OpenAI embedding model when OpenAI is selected without naming one.
    private const string DefaultOpenAIModel = "text-embedding-3-small";

    private readonly OllamaConnection _aspireDefault;
    private readonly IOptionsMonitor<EmbeddingOptions> _options;
    private readonly object _swapLock = new();

    private IEmbeddingGenerator<string, Embedding<float>>? _inner;
    private string? _builtKey;

    /// <param name="aspireDefault">
    /// The Ollama endpoint/model from the Aspire connection string, used as the fallback when no
    /// provider (or an Ollama provider with unset fields) is configured. May be empty when Aspire
    /// supplied nothing — that only matters if the default path is actually exercised.
    /// </param>
    /// <param name="options">Live config; each call reads <see cref="IOptionsMonitor{T}.CurrentValue"/>.</param>
    public ConfigurableEmbeddingGenerator(OllamaConnection aspireDefault, IOptionsMonitor<EmbeddingOptions> options)
    {
        _aspireDefault = aspireDefault;
        _options = options;
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Current().GenerateAsync(values, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        Current().GetService(serviceType, serviceKey);

    public void Dispose()
    {
        lock (_swapLock)
        {
            _inner?.Dispose();
            _inner = null;
            _builtKey = null;
        }
    }

    // Returns the inner generator for the current config snapshot, rebuilding it under the lock only
    // when the snapshot key changed since the last build (double-checked so concurrent callers don't
    // each rebuild). A swapped-out generator is intentionally left undisposed: an in-flight
    // GenerateAsync may still hold it, and config changes are rare (a manual Settings save), so the
    // cost of not eagerly disposing is negligible against the risk of yanking a live call.
    private IEmbeddingGenerator<string, Embedding<float>> Current()
    {
        var snapshot = _options.CurrentValue;
        var key = SnapshotKey(snapshot);

        var inner = _inner;
        if (inner is not null && key == _builtKey)
            return inner;

        lock (_swapLock)
        {
            if (_inner is not null && key == _builtKey)
                return _inner;

            _inner = Build(snapshot);
            _builtKey = key;
            return _inner;
        }
    }

    private IEmbeddingGenerator<string, Embedding<float>> Build(EmbeddingOptions options) =>
        BuildGenerator(options, _aspireDefault);

    /// <summary>
    /// Builds the inner generator for one <see cref="EmbeddingOptions"/> snapshot against the given
    /// Aspire fallback connection — the single place provider selection/construction lives, shared by
    /// this generator's live path and SYNTH-22's probe-before-persist path (so the endpoint validates a
    /// candidate config with exactly the same construction logic instead of duplicating it). Constructing
    /// the returned client opens no socket; an incomplete config yields a <see cref="NotConfiguredEmbeddingGenerator"/>
    /// that only throws when actually used.
    /// </summary>
    public static IEmbeddingGenerator<string, Embedding<float>> BuildGenerator(EmbeddingOptions options, OllamaConnection aspireDefault)
    {
        var provider = options.Provider?.Trim();

        if (string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
            return BuildOpenAI(options.OpenAI);

        if (string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            return BuildOllama(options.Ollama.Endpoint, options.Ollama.Model, aspireDefault);

        // No/unknown provider: keep today's behavior — Ollama via the Aspire-supplied endpoint/model.
        return BuildOllama(null, null, aspireDefault);
    }

    /// <summary>
    /// The effective Ollama base endpoint for a given config snapshot: the <c>Embedding:Ollama:Endpoint</c>
    /// override when set, otherwise the Aspire-supplied embeddings connection endpoint (null when neither
    /// is available). This is the exact same resolution the live Ollama generator uses in
    /// <see cref="BuildOllama"/>, exposed so the Ollama model-picker/pull endpoints (SYNTH-50) talk to the
    /// same server the embeddings actually use rather than re-deriving it.
    /// </summary>
    public static string? ResolveOllamaEndpoint(EmbeddingOptions options, OllamaConnection aspireDefault) =>
        FirstNonEmpty(options.Ollama.Endpoint, aspireDefault.Endpoint);

    private static IEmbeddingGenerator<string, Embedding<float>> BuildOllama(
        string? endpointOverride, string? modelOverride, OllamaConnection aspireDefault)
    {
        var endpoint = FirstNonEmpty(endpointOverride, aspireDefault.Endpoint);
        var model = FirstNonEmpty(modelOverride, aspireDefault.Model);

        if (string.IsNullOrWhiteSpace(endpoint))
            return new NotConfiguredEmbeddingGenerator(
                "no Ollama endpoint is configured and no Aspire embeddings connection is available");

        // OllamaApiClient implements IEmbeddingGenerator<string, Embedding<float>> and connects lazily,
        // so constructing it opens no socket (same guarantee AddOllamaApiClient relied on).
        return new OllamaApiClient(new Uri(endpoint), model ?? string.Empty);
    }

    private static IEmbeddingGenerator<string, Embedding<float>> BuildOpenAI(EmbeddingOptions.OpenAIEmbeddingOptions openai)
    {
        if (string.IsNullOrWhiteSpace(openai.ApiKey))
            return new NotConfiguredEmbeddingGenerator("OpenAI is selected but no API key is configured");

        var model = FirstNonEmpty(openai.Model, DefaultOpenAIModel)!;
        return new OpenAI.Embeddings.EmbeddingClient(model, openai.ApiKey).AsIEmbeddingGenerator();
    }

    // A fingerprint of every field that selects/parameterizes the inner generator, so a rebuild happens
    // exactly when one of them changes. Held in memory only, never logged (the OpenAI key is in here).
    private string SnapshotKey(EmbeddingOptions o) => string.Join(
        KeySeparator,
        o.Provider?.Trim().ToLowerInvariant() ?? string.Empty,
        o.Ollama.Endpoint ?? string.Empty, o.Ollama.Model ?? string.Empty,
        o.OpenAI.ApiKey ?? string.Empty, o.OpenAI.Model ?? string.Empty,
        _aspireDefault.Endpoint ?? string.Empty, _aspireDefault.Model ?? string.Empty);

    private static string? FirstNonEmpty(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a : (!string.IsNullOrWhiteSpace(b) ? b : null);
}

/// <summary>
/// The Ollama endpoint + model parsed from the Aspire <c>embeddings</c> connection string
/// (<c>Endpoint=...;Model=...</c>). Both may be null when Aspire supplied nothing.
/// </summary>
public sealed record OllamaConnection(string? Endpoint, string? Model)
{
    /// <summary>Parses the Aspire-style <c>Endpoint=...;Model=...</c> connection string (keys case-insensitive).</summary>
    public static OllamaConnection Parse(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return new OllamaConnection(null, null);

        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        return new OllamaConnection(Value(builder, "Endpoint"), Value(builder, "Model"));

        static string? Value(DbConnectionStringBuilder b, string key) =>
            b.TryGetValue(key, out var v) ? v?.ToString() : null;
    }
}

/// <summary>
/// Placeholder generator returned when the embedding config is incomplete/invalid. Constructing and
/// resolving it is side-effect free; only an actual <see cref="GenerateAsync"/> call throws, so a bad
/// config never blocks app startup (matching Aspire's "start clean, no live dependency required" guarantee).
/// </summary>
internal sealed class NotConfiguredEmbeddingGenerator(string reason) : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException($"Embedding generator is not configured: {reason}.");

    // Metadata/service probes must stay side-effect free (DI resolution, telemetry) — never throw here.
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
