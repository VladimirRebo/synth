using Synth.Api;
using Synth.Domain;
using Synth.Domain.Vcs;
using Synth.Infrastructure.Vcs;

namespace Synth.Api.Tests;

// Proves the fallback every MCP tool/REST read endpoint shares for an omitted `collection`
// argument. Found live: every such tool silently returned empty results (not an error) once
// nothing was ever indexed under the literal "default" name — the common case once repoUrl-
// sourced collections (named after the repo, e.g. "github-com-owner-repo") are the only thing
// indexed. CollectionNameResolver replaces the old blind "use CollectionNames.Default" fallback.
public class CollectionNameResolverTests
{
    private static RepositoryEntry Entry(string collection) => new()
    {
        Collection = collection,
        SourceType = "local",
        Source = $"/tmp/{collection}",
        LastIndexedAt = DateTime.UtcNow,
        ChunkCount = 0,
    };

    [Fact]
    public void An_explicit_non_blank_collection_always_wins()
    {
        var entries = new[] { Entry("repo-a"), Entry("repo-b") };

        Assert.Equal("repo-b", CollectionNameResolver.Resolve("repo-b", entries));
    }

    [Fact]
    public void A_null_collection_resolves_to_CollectionNames_Default_when_it_exists()
    {
        var entries = new[] { Entry(CollectionNames.Default), Entry("repo-a") };

        Assert.Equal(CollectionNames.Default, CollectionNameResolver.Resolve(null, entries));
    }

    [Fact]
    public void A_blank_collection_resolves_to_the_sole_indexed_collection_when_default_does_not_exist()
    {
        // The common case today: repoUrl-sourced indexing never lands under "default", so a caller
        // that omits `collection` used to silently query an empty, nonexistent collection.
        var entries = new[] { Entry("github-com-owner-repo") };

        Assert.Equal("github-com-owner-repo", CollectionNameResolver.Resolve("   ", entries));
    }

    [Fact]
    public void An_unset_collection_falls_back_to_CollectionNames_Default_when_ambiguous_with_multiple_repos()
    {
        var entries = new[] { Entry("repo-a"), Entry("repo-b") };

        // Neither is "the" main codebase — resolving to CollectionNames.Default here (which won't
        // match either) preserves the old "unknown collection" behavior instead of guessing wrong.
        Assert.Equal(CollectionNames.Default, CollectionNameResolver.Resolve(null, entries));
    }

    [Fact]
    public void An_unset_collection_falls_back_to_CollectionNames_Default_when_nothing_is_indexed()
    {
        Assert.Equal(CollectionNames.Default, CollectionNameResolver.Resolve(null, []));
    }

    [Fact]
    public void The_literal_default_string_is_treated_the_same_as_unset()
    {
        // CallGraphTool's collection parameter can't default to null (C# requires a compile-time
        // constant), so it defaults to the literal "default" string instead — this must resolve
        // exactly like an omitted argument, not like a real explicit request for that name.
        var entries = new[] { Entry("github-com-owner-repo") };

        Assert.Equal("github-com-owner-repo", CollectionNameResolver.Resolve(CollectionNames.Default, entries));
    }

    [Fact]
    public async Task ResolveAsync_reads_the_registry_and_delegates_to_the_same_logic()
    {
        var registry = new InMemoryRepositoryRegistry();
        await registry.UpsertAsync(Entry("github-com-owner-repo"));

        Assert.Equal("github-com-owner-repo", await CollectionNameResolver.ResolveAsync(null, registry));
    }
}
