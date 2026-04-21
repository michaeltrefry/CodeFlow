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
        repo.Owner.Should().Be("foo");
        repo.Name.Should().Be("bar");
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
