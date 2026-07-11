using System.Text;

namespace Synth.Domain.Vcs;

/// <summary>
/// The parsed shape of a git remote URL in HTTPS form (e.g.
/// <c>https://github.com/owner/repo.git</c> or <c>https://gitlab.com/group/subgroup/repo.git</c>).
/// Also handles <c>file://</c> URLs so the same slugging logic can name a local-fixture checkout.
/// SSH-form remotes (<c>git@host:owner/repo.git</c>) are intentionally out of scope (SYNTH-18).
/// </summary>
/// <remarks>
/// <see cref="Slug"/> is a stable, Qdrant-safe collection name derived from host + path, so
/// re-indexing the same repository always maps to the same collection (SYNTH-17 keys chunks by
/// collection). The sanitization mirrors <c>QdrantCodeChunkStore.SanitizeCollectionName</c>.
/// </remarks>
public sealed record RepoUrlInfo
{
    /// <summary>The remote host, e.g. <c>github.com</c>. Empty for <c>file://</c> URLs.</summary>
    public required string Host { get; init; }

    /// <summary>
    /// The path segments between host and repo, joined with <c>/</c>, e.g. <c>owner</c> or the
    /// nested GitLab group path <c>group/subgroup</c>. Empty when the repo sits directly under the host.
    /// </summary>
    public required string Owner { get; init; }

    /// <summary>The repository name with any trailing <c>.git</c> stripped, e.g. <c>repo</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Provider classification derived from <see cref="Host"/>.</summary>
    public required GitProvider Provider { get; init; }

    /// <summary>
    /// Stable, deterministic, Qdrant-safe slug derived from host + path — the collection name for
    /// this repository. The same URL always yields the same slug regardless of a trailing <c>.git</c>.
    /// </summary>
    public required string Slug { get; init; }

    /// <summary>
    /// Parses <paramref name="url"/> (HTTPS or <c>file://</c> form). Throws <see cref="FormatException"/>
    /// for relative or SSH-form URLs, or when no repository path is present.
    /// </summary>
    public static RepoUrlInfo Parse(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            throw new FormatException(
                $"'{url}' is not an absolute URL. SSH-form remotes (git@host:owner/repo.git) are not supported.");
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (segments.Count == 0)
            throw new FormatException($"'{url}' has no repository path.");

        var name = segments[^1];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            name = name[..^".git".Length];
        if (string.IsNullOrEmpty(name))
            throw new FormatException($"'{url}' has an empty repository name.");
        segments[^1] = name;

        var host = uri.Host;
        return new RepoUrlInfo
        {
            Host = host,
            Owner = segments.Count > 1 ? string.Join('/', segments[..^1]) : string.Empty,
            Name = name,
            Provider = ClassifyProvider(host),
            Slug = DeriveSlug(host, segments),
        };
    }

    /// <summary>Non-throwing variant of <see cref="Parse"/>.</summary>
    public static bool TryParse(string? url, out RepoUrlInfo? info)
    {
        try
        {
            info = string.IsNullOrWhiteSpace(url) ? null : Parse(url);
            return info is not null;
        }
        catch (FormatException)
        {
            info = null;
            return false;
        }
    }

    private static GitProvider ClassifyProvider(string host) =>
        host.Contains("github", StringComparison.OrdinalIgnoreCase) ? GitProvider.GitHub
        : host.Contains("gitlab", StringComparison.OrdinalIgnoreCase) ? GitProvider.GitLab
        : GitProvider.Other;

    // Lowercase, keep [a-z0-9_-], map anything else to '-', trim stray leading/trailing dashes.
    // Same character class as QdrantCodeChunkStore's sanitizer so the slug survives it unchanged.
    private static string DeriveSlug(string host, IReadOnlyList<string> segments)
    {
        var raw = string.Join('/', new[] { host }.Concat(segments).Where(s => !string.IsNullOrEmpty(s)));

        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw.ToLowerInvariant())
        {
            builder.Append(ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' ? ch : '-');
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrEmpty(slug) ? "repo" : slug;
    }
}
