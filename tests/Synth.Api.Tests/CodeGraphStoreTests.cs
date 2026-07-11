using Synth.Api.Graph;
using Synth.Domain.Graph;

namespace Synth.Api.Tests;

// Round-trip + isolation + replace coverage of the ICodeGraphStore contract against the in-memory
// implementation (the production fallback used when no Mongo is configured, and the same shape
// MongoCodeGraphStore exposes). No live Mongo required — matching how this repo tests its
// Mongo-backed stores (see RepositoryRegistryTests): the Mongo implementation is exercised only in
// integration, never against a live server here.
public class CodeGraphStoreTests
{
    private static CallEdge Edge(string collection, string caller, string callee) =>
        new(collection, caller, callee, $"{caller}.cs", 1);

    [Fact]
    public async Task Replace_then_find_callers_returns_edges_into_the_symbol()
    {
        var store = new InMemoryCodeGraphStore();
        await store.ReplaceEdgesAsync("repo", [Edge("repo", "A.Caller", "A.Target"), Edge("repo", "B.Other", "A.Target")]);

        var callers = await store.FindCallersAsync("repo", "A.Target");

        Assert.Equal(2, callers.Count);
        Assert.All(callers, e => Assert.Equal("A.Target", e.Callee));
        Assert.Contains(callers, e => e.Caller == "A.Caller");
        Assert.Contains(callers, e => e.Caller == "B.Other");
    }

    [Fact]
    public async Task Replace_then_find_callees_returns_edges_out_of_the_symbol()
    {
        var store = new InMemoryCodeGraphStore();
        await store.ReplaceEdgesAsync("repo", [Edge("repo", "A.Source", "X.One"), Edge("repo", "A.Source", "X.Two")]);

        var callees = await store.FindCalleesAsync("repo", "A.Source");

        Assert.Equal(2, callees.Count);
        Assert.All(callees, e => Assert.Equal("A.Source", e.Caller));
        Assert.Contains(callees, e => e.Callee == "X.One");
        Assert.Contains(callees, e => e.Callee == "X.Two");
    }

    [Fact]
    public async Task Replace_genuinely_replaces_leaving_none_of_the_first_set()
    {
        var store = new InMemoryCodeGraphStore();
        await store.ReplaceEdgesAsync("repo", [Edge("repo", "Old.Caller", "Shared.Target")]);
        await store.ReplaceEdgesAsync("repo", [Edge("repo", "New.Caller", "Shared.Target")]);

        var callers = await store.FindCallersAsync("repo", "Shared.Target");

        var found = Assert.Single(callers);
        Assert.Equal("New.Caller", found.Caller);
        Assert.DoesNotContain(callers, e => e.Caller == "Old.Caller");
    }

    [Fact]
    public async Task Replace_with_empty_set_clears_a_collections_edges()
    {
        var store = new InMemoryCodeGraphStore();
        await store.ReplaceEdgesAsync("repo", [Edge("repo", "A", "B")]);

        await store.ReplaceEdgesAsync("repo", []);

        Assert.Empty(await store.FindCallersAsync("repo", "B"));
        Assert.Empty(await store.FindCalleesAsync("repo", "A"));
    }

    [Fact]
    public async Task Edges_are_isolated_per_collection()
    {
        var store = new InMemoryCodeGraphStore();
        await store.ReplaceEdgesAsync("one", [Edge("one", "One.Caller", "Common.Target")]);
        await store.ReplaceEdgesAsync("two", [Edge("two", "Two.Caller", "Common.Target")]);

        var oneCallers = await store.FindCallersAsync("one", "Common.Target");
        var twoCallers = await store.FindCallersAsync("two", "Common.Target");

        Assert.Equal("One.Caller", Assert.Single(oneCallers).Caller);
        Assert.Equal("Two.Caller", Assert.Single(twoCallers).Caller);
    }

    [Fact]
    public async Task Replacing_one_collection_does_not_disturb_another()
    {
        var store = new InMemoryCodeGraphStore();
        await store.ReplaceEdgesAsync("one", [Edge("one", "One.Caller", "T")]);
        await store.ReplaceEdgesAsync("two", [Edge("two", "Two.Caller", "T")]);

        // Re-index "one" — "two" must be untouched.
        await store.ReplaceEdgesAsync("one", [Edge("one", "One.NewCaller", "T")]);

        Assert.Equal("Two.Caller", Assert.Single(await store.FindCallersAsync("two", "T")).Caller);
        Assert.Equal("One.NewCaller", Assert.Single(await store.FindCallersAsync("one", "T")).Caller);
    }

    [Fact]
    public async Task Find_on_unknown_collection_or_symbol_is_empty()
    {
        var store = new InMemoryCodeGraphStore();
        await store.ReplaceEdgesAsync("repo", [Edge("repo", "A", "B")]);

        Assert.Empty(await store.FindCallersAsync("missing", "B"));
        Assert.Empty(await store.FindCallersAsync("repo", "Nope"));
        Assert.Empty(await store.FindCalleesAsync("repo", "Nope"));
    }
}
