using System.Text.Json;
using Synth.Domain.Embeddings;
using Synth.Core.Vcs;
using Synth.Domain.Configuration;
using Synth.Domain.Vcs;
using Synth.Infrastructure.Configuration;

namespace Synth.Api.Configuration;

/// <summary>
/// Maps <c>GET</c>/<c>PUT /settings/raw</c>: an advanced escape hatch that reads and replaces the
/// <b>entire</b> stored config document as plain JSON, secrets included <b>unmasked</b> — the opposite
/// of the per-section endpoints (<c>/settings/vcs</c>, <c>/settings/embedding</c>), which mask secrets
/// for the common case. Synth is a single local user with no auth and the values already sit in
/// plaintext in Mongo/the config file, so this is a UX affordance, not a broken security boundary
/// (2026-07-09 decision, SYNTH-29).
/// <para>
/// Unlike <c>/settings/embedding</c> there is deliberately <b>no probe-before-persist</b>: a raw JSON
/// editor trusts the caller with the values and validates only that the body is a well-formed JSON
/// object. Writes go through <see cref="ConfigSectionUpdater.ReplaceDocumentAsync"/> so a whole-document
/// replace can't race a concurrent section <c>PUT</c>, and the store's <c>Changed</c> event reloads
/// <c>IOptionsMonitor&lt;T&gt;</c> live — the same reload path the section endpoints rely on.
/// </para>
/// </summary>
public static class RawSettingsEndpoints
{
    // Response header carrying non-fatal warnings about a persisted document (currently: top-level
    // keys that match no config section this app reads). A header keeps the PUT body the bare
    // document text — the raw editor client consumes the response via response.text() — so surfacing
    // warnings this way is purely additive and doesn't break that integration.
    public const string WarningsHeader = "X-Settings-Warnings";

    // Config sections this build actually binds. A top-level key outside this set is stored fine
    // (the raw editor is deliberately forward-compatible) but is worth flagging as a likely typo.
    private static readonly string[] KnownSectionNames = [VcsOptions.SectionName, EmbeddingOptions.SectionName];

    public static IEndpointRouteBuilder MapRawSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // GET /settings/raw — the whole stored document as-is (unmasked), "{}" when nothing is stored.
        endpoints.MapGet("/settings/raw", async (ConfigSectionUpdater updater, CancellationToken cancellationToken) =>
        {
            var document = await updater.LoadDocumentAsync(cancellationToken);
            return Results.Content(document, "application/json");
        });

        // PUT /settings/raw — replace the whole document. Read the raw request body verbatim (so what a
        // subsequent GET returns is exactly what was sent) rather than binding a JsonElement and
        // re-serializing; ReplaceDocumentAsync validates it parses as a JSON object before persisting.
        endpoints.MapPut("/settings/raw", async (HttpRequest request, ConfigSectionUpdater updater, CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);

            try
            {
                await updater.ReplaceDocumentAsync(body, cancellationToken);
            }
            catch (FormatException ex)
            {
                // Malformed or non-object JSON never reached the store; report it as a 400.
                return Results.BadRequest(new { error = ex.Message });
            }

            // Echo the persisted document (unmasked). Built from the store after the save, so a correct
            // body here also proves the write landed on the same document the reload path reads.
            var persisted = await updater.LoadDocumentAsync(cancellationToken);

            // Non-blocking warning pass: the write already happened above, so a typo like a top-level
            // "Vsc" section that binds to nothing is at least visible instead of silently swallowed.
            var warnings = CollectUnknownKeyWarnings(persisted);
            if (warnings.Count > 0)
            {
                // Serialized as a JSON array; default escaping keeps it header-safe (ASCII) even for
                // non-ASCII key names. Kept out of the body so the response stays the bare document text.
                request.HttpContext.Response.Headers[WarningsHeader] = JsonSerializer.Serialize(warnings);
            }

            return Results.Content(persisted, "application/json");
        });

        return endpoints;
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
