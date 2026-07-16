using Synth.Domain;
using Synth.Domain.Vcs;

namespace Synth.Api;

/// <summary>
/// Resolves the optional <c>collection</c> argument shared by every MCP tool and REST read
/// endpoint: an explicit non-blank value always wins. Otherwise falls back to
/// <see cref="CollectionNames.Default"/> only when it actually exists in the registry — repoUrl-
/// sourced collections (the common case today) never land there, only local-path indexing does —
/// or to the registry's sole collection when exactly one is indexed, so "leave unset to search the
/// main indexed codebase" means what it says even when nothing was ever indexed under the literal
/// "default" name. Genuinely ambiguous cases (no collections, or several non-default ones) fall
/// through to <see cref="CollectionNames.Default"/> unchanged, leaving the store's own
/// unknown-collection handling (empty results / 404) to apply as before.
/// </summary>
public static class CollectionNameResolver
{
    public static async Task<string> ResolveAsync(
        string? collection, IRepositoryRegistry registry, CancellationToken cancellationToken = default) =>
        Resolve(collection, await registry.ListAsync(cancellationToken));

    public static string Resolve(string? collection, IReadOnlyList<RepositoryEntry> entries)
    {
        if (!string.IsNullOrWhiteSpace(collection) && collection != CollectionNames.Default)
            return collection;

        if (entries.Any(e => e.Collection == CollectionNames.Default))
            return CollectionNames.Default;

        return entries.Count == 1 ? entries[0].Collection : CollectionNames.Default;
    }
}
