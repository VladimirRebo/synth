using System.Text.Json;
using System.Text.Json.Nodes;
using Synth.Domain.Configuration;

namespace Synth.Infrastructure.Configuration;

/// <summary>
/// Thread-safe read-merge-write of a single top-level section of the <see cref="IConfigStore"/>'s
/// JSON document. Loads the current document (or starts from <c>{}</c> when nothing is stored yet),
/// hands the caller the named section object to mutate in place, then re-serializes and persists the
/// whole document via <see cref="IConfigStore.SaveAsync"/>. The store raises <c>Changed</c> on save,
/// which the <see cref="ConfigStoreConfigurationProvider"/> turns into an <c>IConfiguration</c>
/// reload — so <c>IOptionsMonitor&lt;T&gt;</c> subscribers observe the change without a restart
/// (no separate <c>IConfigurationRoot.Reload()</c> is needed).
///
/// A lock serialises the read-modify-write so two concurrent updates can't read the same document
/// and clobber each other's section — the same reasoning as Sonar's thread-safe section update.
/// Registered as a singleton and reusable across sections (e.g. the <c>Embedding</c> section in a
/// later Settings task), not just <c>Vcs</c>.
/// </summary>
public sealed class ConfigSectionUpdater(IConfigStore store)
{
    // SemaphoreSlim rather than `lock`, because the critical section spans awaits (load + save).
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Merges one top-level section into the stored document. <paramref name="mutate"/> receives the
    /// existing section object (an empty <see cref="JsonObject"/> when the section is absent) and
    /// mutates it in place; every other section of the document is left untouched.
    /// </summary>
    public async Task UpdateSectionAsync(
        string sectionName,
        Action<JsonObject> mutate,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var json = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var root = string.IsNullOrWhiteSpace(json)
                ? new JsonObject()
                : JsonNode.Parse(json)!.AsObject();

            if (root[sectionName] is not JsonObject section)
            {
                section = new JsonObject();
                root[sectionName] = section;
            }

            mutate(section);

            await store.SaveAsync(root.ToJsonString(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns the whole stored document as-is (secrets included — the raw editor is a deliberate
    /// power-user escape hatch, unlike the masked section endpoints), or <c>"{}"</c> when nothing is
    /// stored yet — the same empty-document default <see cref="UpdateSectionAsync"/> starts from.
    /// Reads under <see cref="_gate"/> so it can't observe a half-written document mid-replace.
    /// </summary>
    public async Task<string> LoadDocumentAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var json = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(json) ? "{}" : json;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Replaces the entire stored document with <paramref name="json"/>. Validates that the input is a
    /// well-formed JSON <em>object</em> before touching the store — the raw editor trusts the caller with
    /// the section <em>values</em>, but a malformed or non-object document would break the config
    /// provider's flattening, so it is rejected with a <see cref="FormatException"/> (which the endpoint
    /// turns into a 400) and never reaches <see cref="IConfigStore.SaveAsync"/>. On success the exact
    /// input text is persisted under the same <see cref="_gate"/> as <see cref="UpdateSectionAsync"/>, so
    /// a whole-document write can't race a concurrent section update and clobber it.
    /// </summary>
    public async Task ReplaceDocumentAsync(string json, CancellationToken cancellationToken = default)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Request body must be well-formed JSON.", ex);
        }

        if (node is not JsonObject)
            throw new FormatException("Request body must be a JSON object.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await store.SaveAsync(json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
