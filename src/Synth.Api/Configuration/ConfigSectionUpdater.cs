using System.Text.Json.Nodes;

namespace Synth.Api.Configuration;

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
}
