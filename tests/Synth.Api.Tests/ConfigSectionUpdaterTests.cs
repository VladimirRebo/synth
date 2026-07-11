using System.Text.Json;
using System.Text.Json.Nodes;
using Synth.Api.Configuration;
using Synth.Domain.Configuration;

namespace Synth.Api.Tests;

// Unit tests for the thread-safe section merge helper: it must create a section from an empty
// document, replace only the named top-level section, and serialise a race-free result under
// concurrent updates.
public class ConfigSectionUpdaterTests
{
    [Fact]
    public async Task Creates_the_section_when_the_document_is_empty()
    {
        var store = new InMemoryConfigStore();
        var updater = new ConfigSectionUpdater(store);

        await updater.UpdateSectionAsync("Vcs", section => section["WorkspaceRoot"] = "/tmp/synth");

        using var document = JsonDocument.Parse(store.Current!);
        Assert.Equal("/tmp/synth", document.RootElement.GetProperty("Vcs").GetProperty("WorkspaceRoot").GetString());
    }

    [Fact]
    public async Task Merges_only_the_named_section_and_leaves_others_untouched()
    {
        var store = new InMemoryConfigStore("""{ "Embedding": { "Model": "nomic" }, "Vcs": { "WorkspaceRoot": "/old" } }""");
        var updater = new ConfigSectionUpdater(store);

        await updater.UpdateSectionAsync("Vcs", section => section["WorkspaceRoot"] = "/new");

        using var document = JsonDocument.Parse(store.Current!);
        Assert.Equal("/new", document.RootElement.GetProperty("Vcs").GetProperty("WorkspaceRoot").GetString());
        Assert.Equal("nomic", document.RootElement.GetProperty("Embedding").GetProperty("Model").GetString());
    }

    [Fact]
    public async Task Serialises_concurrent_updates_without_losing_writes()
    {
        var store = new InMemoryConfigStore();
        var updater = new ConfigSectionUpdater(store);

        // 50 concurrent updates each set a distinct key; the lock must let every write survive.
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            updater.UpdateSectionAsync("Vcs", section => section[$"Key{i}"] = i)));

        var section = JsonNode.Parse(store.Current!)!.AsObject()["Vcs"]!.AsObject();
        Assert.Equal(50, section.Count);
        for (var i = 0; i < 50; i++)
            Assert.Equal(i, (int)section[$"Key{i}"]!);
    }
}
