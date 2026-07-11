namespace Synth.Core.Vcs;

/// <summary>
/// Builds a provider blob URL that points at a chunk's line range in the remote repository, so a
/// search result can link straight to the matched code on GitHub/GitLab. Pure and side-effect free —
/// derives everything from an already-parsed <see cref="RepoUrlInfo"/> (avoids re-parsing the raw URL
/// and its trailing-<c>.git</c> edge cases). Only GitHub and GitLab have known blob-URL shapes;
/// every other provider yields <c>null</c>, matching Synth's GitHub/GitLab-only VCS scope (SYNTH-40).
/// </summary>
public static class SourceUrlBuilder
{
    /// <summary>
    /// Builds the blob URL for the <paramref name="startLine"/>–<paramref name="endLine"/> span of
    /// <paramref name="relativePath"/> in the repository described by <paramref name="info"/>.
    /// </summary>
    /// <param name="branch">
    /// The indexed branch, or <c>null</c>/whitespace when the repo's default branch was used — in
    /// which case the literal <c>HEAD</c> segment is emitted (both providers resolve
    /// <c>/blob/HEAD/...</c> to the default branch).
    /// </param>
    /// <returns>
    /// The blob URL, or <c>null</c> for <see cref="GitProvider.Other"/> (no known blob-URL shape).
    /// </returns>
    public static string? Build(RepoUrlInfo info, string? branch, string relativePath, int startLine, int endLine)
    {
        ArgumentNullException.ThrowIfNull(info);

        // Default branch -> the literal HEAD segment; both GitHub and GitLab resolve /blob/HEAD/... .
        var reference = string.IsNullOrWhiteSpace(branch) ? "HEAD" : branch;

        // Normalize to forward slashes and drop any leading slash so it slots cleanly after /blob/{ref}/.
        var path = relativePath.Replace('\\', '/').TrimStart('/');

        // Owner is empty when the repo sits directly under the host; skip the empty segment so the URL
        // never contains a double slash.
        var repoPath = string.IsNullOrEmpty(info.Owner) ? info.Name : $"{info.Owner}/{info.Name}";

        return info.Provider switch
        {
            GitProvider.GitHub => $"https://{info.Host}/{repoPath}/blob/{reference}/{path}#L{startLine}-L{endLine}",
            GitProvider.GitLab => $"https://{info.Host}/{repoPath}/-/blob/{reference}/{path}#L{startLine}-{endLine}",
            _ => null,
        };
    }
}
