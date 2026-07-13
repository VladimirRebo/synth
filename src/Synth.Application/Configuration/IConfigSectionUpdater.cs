using System.Text.Json.Nodes;

namespace Synth.Application.Configuration;

/// <summary>
/// Application-layer port over Infrastructure's <c>ConfigSectionUpdater</c>: a thread-safe
/// read-merge-write of the config-store document. Command handlers in this layer
/// (e.g. <see cref="Vcs.UpdateVcsSettingsCommandHandler"/>,
/// <see cref="ReplaceRawSettingsCommandHandler"/>) depend on this port rather than the concrete
/// updater so Application never references Infrastructure — the same seam <c>IGitRepoService</c>
/// provides over <c>GitRepoService</c>. Exposes both the per-section merge and the whole-document
/// load/replace pair the raw-settings flow needs (SYNTH-71 moved that flow behind the CQRS seam, so
/// its handler now depends on the port instead of the concrete type).
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

    /// <summary>
    /// Returns the whole stored document as-is (secrets included — the raw editor is a deliberate
    /// power-user escape hatch, unlike the masked section endpoints), or <c>"{}"</c> when nothing is
    /// stored yet.
    /// </summary>
    Task<string> LoadDocumentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the entire stored document with <paramref name="json"/>. Validates that the input is a
    /// well-formed JSON <em>object</em> before touching the store, rejecting anything else with a
    /// <see cref="FormatException"/> (which the controller turns into a 400); nothing is persisted on
    /// failure.
    /// </summary>
    Task ReplaceDocumentAsync(string json, CancellationToken cancellationToken = default);
}
