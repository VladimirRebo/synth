using System.Text.Json;
using Synth.Application.Cqrs;
using Synth.Domain.Embeddings;
using Synth.Domain.Vcs;

namespace Synth.Application.Configuration;

/// <summary>
/// Handles <see cref="ReplaceRawSettingsCommand"/>: the whole-document replace behind
/// <c>PUT /settings/raw</c>. Unlike the section endpoints there is deliberately <b>no
/// probe-before-persist</b> — a raw JSON editor trusts the caller with the values and
/// <see cref="IConfigSectionUpdater.ReplaceDocumentAsync"/> validates only that the body is a
/// well-formed JSON object (a <see cref="FormatException"/> otherwise, mapped to <c>400</c> at the
/// controller). After the write lands, a non-blocking pass flags any top-level key that matches no
/// config section this build reads, surfaced as informational warnings rather than blocking the write.
/// <para>
/// SYNTH-71 lifted this out of <c>RawSettingsEndpoints</c>'s PUT handler essentially unchanged so it
/// lives behind the CQRS seam (issue #82), following the pattern
/// <see cref="Vcs.UpdateVcsSettingsCommandHandler"/> established: it depends on the
/// <see cref="IConfigSectionUpdater"/> port rather than the concrete <c>ConfigSectionUpdater</c> so
/// Application never references Infrastructure.
/// </para>
/// </summary>
public sealed class ReplaceRawSettingsCommandHandler
    : ICommandHandler<ReplaceRawSettingsCommand, ReplaceRawSettingsResult>
{
    // Config sections this build actually binds. A top-level key outside this set is stored fine
    // (the raw editor is deliberately forward-compatible) but is worth flagging as a likely typo.
    private static readonly string[] KnownSectionNames = [VcsOptions.SectionName, EmbeddingOptions.SectionName];

    private readonly IConfigSectionUpdater _updater;

    public ReplaceRawSettingsCommandHandler(IConfigSectionUpdater updater)
    {
        _updater = updater;
    }

    public async Task<ReplaceRawSettingsResult> HandleAsync(
        ReplaceRawSettingsCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            await _updater.ReplaceDocumentAsync(command.Body, cancellationToken);
        }
        catch (FormatException ex)
        {
            // Malformed or non-object JSON never reached the store; report it as a 400.
            return ReplaceRawSettingsResult.ValidationError(ex.Message);
        }

        // Echo the persisted document (unmasked). Built from the store after the save, so a correct
        // body here also proves the write landed on the same document the reload path reads.
        var persisted = await _updater.LoadDocumentAsync(cancellationToken);

        // Non-blocking warning pass: the write already happened above, so a typo like a top-level
        // "Vsc" section that binds to nothing is at least visible instead of silently swallowed.
        var warnings = CollectUnknownKeyWarnings(persisted);

        return ReplaceRawSettingsResult.Ok(persisted, warnings);
    }

    // Reports a warning for every top-level key of <paramref name="document"/> that doesn't match a
    // known section name (case-insensitively). Never throws: a document that isn't a JSON object
    // (shouldn't happen post-persist) simply yields no warnings.
    private static List<string> CollectUnknownKeyWarnings(string document)
    {
        var warnings = new List<string>();

        JsonElement root;
        try
        {
            using var parsed = JsonDocument.Parse(document);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                return warnings;
            }

            root = parsed.RootElement.Clone();
        }
        catch (JsonException)
        {
            return warnings;
        }

        foreach (var property in root.EnumerateObject())
        {
            var isKnown = KnownSectionNames.Any(name =>
                string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase));
            if (!isKnown)
            {
                warnings.Add(
                    $"Unknown top-level key \"{property.Name}\": no config section by that name is read by this app.");
            }
        }

        return warnings;
    }
}
