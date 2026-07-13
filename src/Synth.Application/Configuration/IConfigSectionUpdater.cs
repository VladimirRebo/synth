using System.Text.Json.Nodes;

namespace Synth.Application.Configuration;

/// <summary>
/// Application-layer port over Infrastructure's <c>ConfigSectionUpdater</c>: a thread-safe
/// read-merge-write of one top-level section of the config-store document. Command handlers in this
/// layer (e.g. <see cref="Vcs.UpdateVcsSettingsCommandHandler"/>) depend on this port rather than the
/// concrete updater so Application never references Infrastructure — the same seam
/// <c>IGitRepoService</c> provides over <c>GitRepoService</c>. Only the single method a handler needs
/// is exposed here; the concrete type keeps its other members (raw-document load/replace) for the
/// endpoints that use it directly.
/// </summary>
public interface IConfigSectionUpdater
{
    /// <summary>
    /// Merges one top-level section into the stored document. <paramref name="mutate"/> receives the
    /// existing section object (an empty <see cref="JsonObject"/> when the section is absent) and
    /// mutates it in place; every other section of the document is left untouched.
    /// </summary>
    Task UpdateSectionAsync(
        string sectionName,
        Action<JsonObject> mutate,
        CancellationToken cancellationToken = default);
}
