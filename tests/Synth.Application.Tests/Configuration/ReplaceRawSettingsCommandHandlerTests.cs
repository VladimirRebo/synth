using System.Text.Json;
using System.Text.Json.Nodes;
using Synth.Application.Configuration;

namespace Synth.Application.Tests.Configuration;

// Proves SYNTH-71: the PUT /settings/raw replace-and-warn body now lives in
// ReplaceRawSettingsCommandHandler, behaving identically to the old RawSettingsEndpoints PUT handler.
// Runs offline against a fake IConfigSectionUpdater (no real IConfigStore), so the FormatException->
// validation mapping and the unknown-top-level-key warning detection are asserted directly on the
// handler — the unit-level counterpart to RawSettingsControllerTests' HTTP round trip.
public class ReplaceRawSettingsCommandHandlerTests
{
    [Fact]
    public async Task Malformed_json_body_is_a_validation_error_and_persists_nothing()
    {
        var updater = new FakeUpdater();
        var handler = new ReplaceRawSettingsCommandHandler(updater);

        var result = await handler.HandleAsync(new ReplaceRawSettingsCommand("{ this is not valid json"));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.False(updater.Replaced); // malformed input never reached the store
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Non_object_json_body_is_a_validation_error_and_persists_nothing()
    {
        var updater = new FakeUpdater();
        var handler = new ReplaceRawSettingsCommandHandler(updater);

        // Well-formed JSON, but an array — the config provider needs a top-level object.
        var result = await handler.HandleAsync(new ReplaceRawSettingsCommand("[1, 2, 3]"));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.False(updater.Replaced);
    }

    [Fact]
    public async Task Only_known_sections_persist_verbatim_without_warnings()
    {
        var updater = new FakeUpdater();
        var handler = new ReplaceRawSettingsCommandHandler(updater);

        const string document =
            """{"Vcs":{"GitHub":{"Token":"ghp_ok"}},"Embedding":{"Provider":"OpenAI"}}""";

        var result = await handler.HandleAsync(new ReplaceRawSettingsCommand(document));

        Assert.True(result.Success);
        Assert.Empty(result.Warnings);
        // The persisted document is echoed back byte-for-byte (secrets unmasked — the raw editor's point).
        Assert.Equal(document, result.Document);
        Assert.Equal(document, updater.Document);
    }

    [Fact]
    public async Task Unknown_top_level_key_still_persists_but_warns_identifying_the_key()
    {
        var updater = new FakeUpdater();
        var handler = new ReplaceRawSettingsCommandHandler(updater);

        // "Typo" matches no section this app reads; "Vcs" is known.
        const string document = """{"Vcs":{"GitHub":{"Token":"ghp_ok"}},"Typo":{"Foo":"bar"}}""";

        var result = await handler.HandleAsync(new ReplaceRawSettingsCommand(document));

        Assert.True(result.Success);
        Assert.Equal(document, updater.Document); // the write is NOT blocked
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("Typo", warning);
        Assert.DoesNotContain("Vcs", warning); // the known section is not flagged
    }

    [Fact]
    public async Task Known_section_names_match_case_insensitively()
    {
        var updater = new FakeUpdater();
        var handler = new ReplaceRawSettingsCommandHandler(updater);

        // Lowercase "vcs"/"embedding" bind case-insensitively, so they are not a typo — no warning.
        const string document = """{"vcs":{"GitHub":{"Token":"ghp_ok"}},"embedding":{"Provider":"OpenAI"}}""";

        var result = await handler.HandleAsync(new ReplaceRawSettingsCommand(document));

        Assert.True(result.Success);
        Assert.Empty(result.Warnings);
    }

    // A pure-Application stand-in for ConfigSectionUpdater: replicates the real updater's validation
    // contract (a FormatException for a malformed or non-object body, nothing stored) and otherwise
    // records the persisted document so LoadDocumentAsync round-trips it. No real IConfigStore.
    private sealed class FakeUpdater : IConfigSectionUpdater
    {
        public string? Document { get; private set; }
        public bool Replaced { get; private set; }

        public Task UpdateSectionAsync(
            string sectionName, Action<JsonObject> mutate, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The raw-settings handler never merges a single section.");

        public Task<string> LoadDocumentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Document ?? "{}");

        public Task ReplaceDocumentAsync(string json, CancellationToken cancellationToken = default)
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

            Document = json;
            Replaced = true;
            return Task.CompletedTask;
        }
    }
}
