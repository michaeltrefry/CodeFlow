using CodeFlow.Runtime.Workspace;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Workspace;

public sealed class RepoReferenceTests
{
    [Fact]
    public void Parse_ShouldExtractHostOwnerAndNameFromHttpsUrl()
    {
        var repo = RepoReference.Parse("https://github.com/acme/widget.git");

        repo.Host.Should().Be("github.com");
        repo.Owner.Should().Be("acme");
        repo.Name.Should().Be("widget");
        repo.Slug.Should().Be("acme-widget");
    }

    [Fact]
    public void Parse_ShouldStripGitSuffix_WhenPresent()
    {
        var repo = RepoReference.Parse("https://example.com/a/b.GIT");

        repo.Name.Should().Be("b");
    }

    [Fact]
    public void Parse_ShouldLowercaseHost()
    {
        var repo = RepoReference.Parse("https://GitHub.com/a/b");

        repo.Host.Should().Be("github.com");
    }

    [Fact]
    public void Parse_ShouldAcceptFileUrls_ForLocalOrigins()
    {
        var repo = RepoReference.Parse("file:///tmp/origins/foo/bar.git");

        repo.Host.Should().Be("local");
        repo.Owner.Should().Be("tmp/origins/foo");
        repo.Name.Should().Be("bar");
    }

    [Fact]
    public void Parse_ShouldPreserveFullPath_ForNestedHttpRepos()
    {
        var repo = RepoReference.Parse("https://gitlab.com/group/subgroup/repo.git");

        repo.Host.Should().Be("gitlab.com");
        repo.Owner.Should().Be("group/subgroup");
        repo.Name.Should().Be("repo");
    }

    [Fact]
    public void IdentityKey_ShouldDiffer_ForRepoUrlsThatWouldCollideOnSluggingAlone()
    {
        var nested = RepoReference.Parse("https://gitlab.com/group/subgroup/repo");
        var flat = RepoReference.Parse("https://gitlab.com/group-subgroup/repo");

        nested.Slug.Should().Be(flat.Slug,
            "the slug intentionally collapses slashes and matches for both URLs — identity must not rely on slug alone");
        nested.IdentityKey.Should().NotBe(flat.IdentityKey,
            "IdentityKey must be collision-free so the two repos get distinct workspaces");
    }

    [Fact]
    public void MirrorRelativePath_ShouldSplitOwnerPathSegments()
    {
        var repo = RepoReference.Parse("https://gitlab.com/group/subgroup/repo");

        var expected = Path.Combine("gitlab.com", "group", "subgroup", "repo.git");
        repo.MirrorRelativePath.Should().Be(expected);
    }

    [Fact]
    public void Parse_ShouldThrow_ForNonHttpNonFileScheme()
    {
        var act = () => RepoReference.Parse("ssh://git@github.com/a/b.git");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_ShouldThrow_ForHttpUrlMissingOwnerAndRepo()
    {
        var act = () => RepoReference.Parse("https://github.com/only-one-segment");

        act.Should().Throw<ArgumentException>();
    }
}
