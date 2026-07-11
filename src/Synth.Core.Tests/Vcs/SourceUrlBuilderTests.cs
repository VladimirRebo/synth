using Synth.Core.Vcs;

namespace Synth.Core.Tests.Vcs;

// SYNTH-40: builds provider blob URLs (GitHub/GitLab) with a line range from an already-parsed
// RepoUrlInfo, so a search result can link straight to the matched code on the remote.
public class SourceUrlBuilderTests
{
    private static RepoUrlInfo Info(string url) => RepoUrlInfo.Parse(url);

    [Fact]
    public void Build_github_with_explicit_branch()
    {
        var url = SourceUrlBuilder.Build(
            Info("https://github.com/owner/repo.git"), "main", "src/App.cs", 10, 20);

        Assert.Equal("https://github.com/owner/repo/blob/main/src/App.cs#L10-L20", url);
    }

    [Fact]
    public void Build_github_with_null_branch_uses_HEAD()
    {
        var url = SourceUrlBuilder.Build(
            Info("https://github.com/owner/repo"), null, "src/App.cs", 1, 1);

        Assert.Equal("https://github.com/owner/repo/blob/HEAD/src/App.cs#L1-L1", url);
    }

    [Fact]
    public void Build_gitlab_uses_dash_blob_shape_and_dash_line_anchor()
    {
        var url = SourceUrlBuilder.Build(
            Info("https://gitlab.com/group/subgroup/repo.git"), "develop", "lib/Thing.cs", 5, 12);

        Assert.Equal("https://gitlab.com/group/subgroup/repo/-/blob/develop/lib/Thing.cs#L5-12", url);
    }

    [Fact]
    public void Build_gitlab_with_null_branch_uses_HEAD()
    {
        var url = SourceUrlBuilder.Build(
            Info("https://gitlab.com/group/repo"), null, "a.cs", 3, 4);

        Assert.Equal("https://gitlab.com/group/repo/-/blob/HEAD/a.cs#L3-4", url);
    }

    [Fact]
    public void Build_other_provider_returns_null()
    {
        var url = SourceUrlBuilder.Build(
            Info("https://git.example.com/team/service.git"), "main", "x.cs", 1, 2);

        Assert.Null(url);
    }

    [Fact]
    public void Build_normalizes_backslash_paths_to_forward_slashes()
    {
        var url = SourceUrlBuilder.Build(
            Info("https://github.com/owner/repo"), "main", "nested\\Bar.cs", 2, 3);

        Assert.Equal("https://github.com/owner/repo/blob/main/nested/Bar.cs#L2-L3", url);
    }
}
