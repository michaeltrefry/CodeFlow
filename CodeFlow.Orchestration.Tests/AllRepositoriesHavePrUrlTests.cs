using CodeFlow.Orchestration;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests;

public sealed class AllRepositoriesHavePrUrlTests
{
    [Fact]
    public void AllRepos_HavePrUrl_ReturnsTrue()
    {
        var json = """
        {
          "repositories": [
            {"url": "https://github.com/foo/bar.git", "prUrl": "https://github.com/foo/bar/pull/1"},
            {"url": "https://github.com/foo/baz.git", "prUrl": "https://github.com/foo/baz/pull/2"}
          ]
        }
        """;

        TraceCleanupConsumer.AllRepositoriesHavePrUrl(json).Should().BeTrue();
    }

    [Fact]
    public void SingleRepo_HasPrUrl_ReturnsTrue()
    {
        var json = """{"repositories": [{"url": "x", "prUrl": "y"}]}""";
        TraceCleanupConsumer.AllRepositoriesHavePrUrl(json).Should().BeTrue();
    }

    [Fact]
    public void EmptyArray_ReturnsFalse()
    {
        var json = """{"repositories": []}""";
        TraceCleanupConsumer.AllRepositoriesHavePrUrl(json).Should().BeFalse(
            "no repos means the workflow wasn't a code-aware run, so cleanup must not fire");
    }

    [Fact]
    public void OneMissingPrUrl_ReturnsFalse()
    {
        var json = """
        {
          "repositories": [
            {"url": "x", "prUrl": "https://github.com/foo/bar/pull/1"},
            {"url": "y"}
          ]
        }
        """;
        TraceCleanupConsumer.AllRepositoriesHavePrUrl(json).Should().BeFalse();
    }

    [Fact]
    public void OneBlankPrUrl_ReturnsFalse()
    {
        var json = """
        {
          "repositories": [
            {"url": "x", "prUrl": "https://github.com/foo/bar/pull/1"},
            {"url": "y", "prUrl": "   "}
          ]
        }
        """;
        TraceCleanupConsumer.AllRepositoriesHavePrUrl(json).Should().BeFalse();
    }

    [Fact]
    public void NoRepositoriesField_ReturnsFalse()
    {
        var json = """{"otherField": 42}""";
        TraceCleanupConsumer.AllRepositoriesHavePrUrl(json).Should().BeFalse();
    }

    [Fact]
    public void NonObjectRoot_ReturnsFalse()
    {
        TraceCleanupConsumer.AllRepositoriesHavePrUrl("[1,2,3]").Should().BeFalse();
    }

    [Fact]
    public void NonStringPrUrl_ReturnsFalse()
    {
        var json = """{"repositories": [{"url": "x", "prUrl": 42}]}""";
        TraceCleanupConsumer.AllRepositoriesHavePrUrl(json).Should().BeFalse();
    }

    [Fact]
    public void MalformedJson_ReturnsFalse()
    {
        TraceCleanupConsumer.AllRepositoriesHavePrUrl("{not json").Should().BeFalse();
    }

    [Fact]
    public void NullOrEmpty_ReturnsFalse()
    {
        TraceCleanupConsumer.AllRepositoriesHavePrUrl(null).Should().BeFalse();
        TraceCleanupConsumer.AllRepositoriesHavePrUrl("").Should().BeFalse();
        TraceCleanupConsumer.AllRepositoriesHavePrUrl("   ").Should().BeFalse();
    }
}
