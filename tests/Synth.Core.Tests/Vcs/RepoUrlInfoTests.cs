using Synth.Core.Vcs;

namespace Synth.Core.Tests.Vcs;

// SYNTH-18: URL parsing + provider classification + stable, Qdrant-safe collection slug derivation.
public class RepoUrlInfoTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo.git", "github.com", "owner", "repo", GitProvider.GitHub, "github-com-owner-repo")]
    [InlineData("https://github.com/owner/repo", "github.com", "owner", "repo", GitProvider.GitHub, "github-com-owner-repo")]
    [InlineData("https://gitlab.com/group/subgroup/repo.git", "gitlab.com", "group/subgroup", "repo", GitProvider.GitLab, "gitlab-com-group-subgroup-repo")]
    [InlineData("https://git.example.com/team/service.git", "git.example.com", "team", "service", GitProvider.Other, "git-example-com-team-service")]
    [InlineData("https://GitHub.com/Owner/Repo.GIT", "github.com", "Owner", "Repo", GitProvider.GitHub, "github-com-owner-repo")]
    public void Parse_extracts_host_owner_name_provider_and_slug(
        string url, string host, string owner, string name, GitProvider provider, string slug)
    {
        var info = RepoUrlInfo.Parse(url);

        Assert.Equal(host, info.Host);
        Assert.Equal(owner, info.Owner);
        Assert.Equal(name, info.Name);
        Assert.Equal(provider, info.Provider);
        Assert.Equal(slug, info.Slug);
    }

    [Fact]
    public void Provider_classification_matches_self_hosted_hosts_by_substring()
    {
        Assert.Equal(GitProvider.GitHub, RepoUrlInfo.Parse("https://github.enterprise.local/o/r.git").Provider);
        Assert.Equal(GitProvider.GitLab, RepoUrlInfo.Parse("https://gitlab.internal.corp/o/r.git").Provider);
    }

    [Fact]
    public void Slug_is_stable_regardless_of_trailing_git_suffix()
    {
        var withSuffix = RepoUrlInfo.Parse("https://github.com/owner/repo.git").Slug;
        var withoutSuffix = RepoUrlInfo.Parse("https://github.com/owner/repo").Slug;

        Assert.Equal(withSuffix, withoutSuffix);
        // Same URL twice is deterministic.
        Assert.Equal(withSuffix, RepoUrlInfo.Parse("https://github.com/owner/repo.git").Slug);
    }

    [Fact]
    public void Different_repos_get_different_slugs()
    {
        Assert.NotEqual(
            RepoUrlInfo.Parse("https://github.com/owner/repo.git").Slug,
            RepoUrlInfo.Parse("https://gitlab.com/owner/repo.git").Slug);
    }

    [Theory]
    [InlineData("git@github.com:owner/repo.git")] // SSH form — intentionally unsupported.
    [InlineData("owner/repo")]                     // relative.
    [InlineData("https://github.com")]             // no repo path.
    public void Parse_rejects_unsupported_or_pathless_urls(string url)
    {
        Assert.Throws<FormatException>(() => RepoUrlInfo.Parse(url));
        Assert.False(RepoUrlInfo.TryParse(url, out var info));
        Assert.Null(info);
    }

    [Fact]
    public void TryParse_returns_true_for_a_valid_url()
    {
        Assert.True(RepoUrlInfo.TryParse("https://github.com/owner/repo.git", out var info));
        Assert.NotNull(info);
        Assert.Equal("repo", info!.Name);
    }
}
