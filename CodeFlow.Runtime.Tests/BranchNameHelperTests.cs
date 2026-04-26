using FluentAssertions;

namespace CodeFlow.Runtime.Tests;

public sealed class BranchNameHelperTests
{
    [Fact]
    public void BranchName_ShortAsciiTitle_ProducesSlugAnd8HexSuffix()
    {
        var result = BranchNameHelper.BranchName("Add todo list", "3b70fc02-1234-5678-9abc-def012345678");
        result.Should().Be("add-todo-list-3b70fc02");
    }

    [Fact]
    public void BranchName_TraceIdWithoutHyphens_StillUsesFirst8Chars()
    {
        var result = BranchNameHelper.BranchName("Hello World", "deadbeef1234567890abcdef00112233");
        result.Should().Be("hello-world-deadbeef");
    }

    [Fact]
    public void BranchName_LongTitle_TruncatesAtWordBoundary()
    {
        var result = BranchNameHelper.BranchName(
            "Refactor the workflow validator to support nested conditional ports",
            "abcdef0011223344");

        var slug = result[..result.LastIndexOf('-')];
        slug.Length.Should().BeLessThanOrEqualTo(BranchNameHelper.MaxSlugLength);
        slug.Should().NotEndWith("-");
        slug.Should().MatchRegex(@"^[a-z0-9-]+$");
        // No mid-word truncation: every dash-segment must be a complete word from the source.
        var sourceWords = "refactor the workflow validator to support nested conditional ports".Split(' ');
        foreach (var part in slug.Split('-'))
        {
            sourceWords.Should().Contain(part);
        }
    }

    [Fact]
    public void BranchName_NonAsciiAccentedTitle_StripsCombiningMarks()
    {
        var result = BranchNameHelper.BranchName("Café résumé", "11223344aabbccdd");
        result.Should().Be("cafe-resume-11223344");
    }

    [Fact]
    public void BranchName_TitleWithOnlySpecials_FallsBackToBranch()
    {
        var result = BranchNameHelper.BranchName("!@#$%^&*()", "11223344aabbccdd");
        result.Should().Be("branch-11223344");
    }

    [Fact]
    public void BranchName_NullOrEmptyTitle_FallsBackToBranch()
    {
        BranchNameHelper.BranchName(null, "11223344aabbccdd").Should().Be("branch-11223344");
        BranchNameHelper.BranchName("", "11223344aabbccdd").Should().Be("branch-11223344");
        BranchNameHelper.BranchName("   ", "11223344aabbccdd").Should().Be("branch-11223344");
    }

    [Fact]
    public void BranchName_NullTraceId_FallsBackToZeros()
    {
        BranchNameHelper.BranchName("Add feature", null).Should().Be("add-feature-00000000");
        BranchNameHelper.BranchName("Add feature", "").Should().Be("add-feature-00000000");
    }

    [Fact]
    public void BranchName_TraceIdShorterThan8Hex_RightPadsWithZeros()
    {
        BranchNameHelper.BranchName("Add feature", "ab12").Should().Be("add-feature-ab120000");
    }

    [Fact]
    public void BranchName_TraceIdInUpperCase_LowercasesPrefix()
    {
        BranchNameHelper.BranchName("Add feature", "ABCDEF0011-2233").Should().Be("add-feature-abcdef00");
    }

    [Fact]
    public void BranchName_StableForSameInputs()
    {
        var a = BranchNameHelper.BranchName("Add todo list", "3b70fc02-1234");
        var b = BranchNameHelper.BranchName("Add todo list", "3b70fc02-1234");
        a.Should().Be(b, "branch_name must be deterministic so multi-repo workflows stay aligned");
    }

    [Fact]
    public void BranchName_RunsOfWhitespaceCollapseToSingleDash()
    {
        BranchNameHelper.BranchName("Add    todo   list", "11223344").Should().Be("add-todo-list-11223344");
    }

    [Fact]
    public void BranchName_LeadingAndTrailingNonWordTrimmed()
    {
        BranchNameHelper.BranchName("---Add Todo---", "11223344").Should().Be("add-todo-11223344");
    }
}
