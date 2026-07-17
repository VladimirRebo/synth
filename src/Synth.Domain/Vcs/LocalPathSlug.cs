using System.Text;
using Synth.Domain;

namespace Synth.Domain.Vcs;

/// <summary>
/// Derives a stable, Qdrant-safe collection slug from a local directory's absolute path — the
/// local-path counterpart to <see cref="RepoUrlInfo.Slug"/>. Before this, every local-path index ran
/// landed in the single <see cref="CollectionNames.Default"/> collection regardless of which
/// directory it was, so indexing a second local directory silently collided with (and, via
/// IndexingPipeline's own-stale-file cleanup, destroyed) the first one's chunks. Slugging the full
/// path the same way <see cref="RepoUrlInfo"/> slugs host + path means two different directories
/// never collide, and the same directory always maps back to the same collection.
/// </summary>
public static class LocalPathSlug
{
    /// <summary>
    /// Slugs <paramref name="path"/>'s full, normalized form (<see cref="Path.GetFullPath(string)"/>)
    /// — so relative paths, a trailing separator, or <c>.</c>/<c>..</c> segments all resolve to the
    /// same slug as their canonical absolute form.
    /// </summary>
    public static string From(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);

        // Same character class and trimming as RepoUrlInfo's slug, so both kinds of collection name
        // look and behave the same way wherever a collection name is used (Qdrant, route segments).
        var builder = new StringBuilder(full.Length);
        foreach (var ch in full.ToLowerInvariant())
        {
            builder.Append(ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' ? ch : '-');
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrEmpty(slug) ? CollectionNames.Default : slug;
    }
}
