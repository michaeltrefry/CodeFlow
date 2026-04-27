using CodeFlow.Orchestration;
using CodeFlow.Persistence;
using FluentAssertions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Orchestration.Tests;

public sealed class RejectionHistoryAccumulatorTests
{
    [Fact]
    public void Append_Markdown_FirstRound_BuildsSingleBlock()
    {
        var result = RejectionHistoryAccumulator.Append(
            existing: null,
            round: 1,
            roundBody: "Findings: missing requirements doc",
            config: new RejectionHistoryConfig(Enabled: true));

        result.Should().Be("## Round 1\nFindings: missing requirements doc");
    }

    [Fact]
    public void Append_Markdown_MultipleRounds_StackChronologically()
    {
        var afterFirst = RejectionHistoryAccumulator.Append(
            existing: null,
            round: 1,
            roundBody: "first feedback",
            config: new RejectionHistoryConfig(Enabled: true));

        var afterSecond = RejectionHistoryAccumulator.Append(
            existing: afterFirst,
            round: 2,
            roundBody: "second feedback",
            config: new RejectionHistoryConfig(Enabled: true));

        afterSecond.Should().Be("## Round 1\nfirst feedback\n\n## Round 2\nsecond feedback");
    }

    [Fact]
    public void Append_Markdown_RedeliveredSameRound_OverwritesNotStacks()
    {
        // Saga redelivery / at-least-once retry safety: appending the same round twice should
        // not produce duplicate blocks — the second call replaces the first round's body.
        var first = RejectionHistoryAccumulator.Append(
            existing: null,
            round: 1,
            roundBody: "initial draft",
            config: new RejectionHistoryConfig(Enabled: true));

        var second = RejectionHistoryAccumulator.Append(
            existing: first,
            round: 1,
            roundBody: "revised feedback",
            config: new RejectionHistoryConfig(Enabled: true));

        second.Should().Be("## Round 1\nrevised feedback");
    }

    [Fact]
    public void Append_Markdown_ExceedsMaxBytes_DropsOldestRound()
    {
        // 32-byte budget: the oldest round must be dropped to make room for the newest.
        var config = new RejectionHistoryConfig(Enabled: true, MaxBytes: 32);

        var afterFirst = RejectionHistoryAccumulator.Append(null, 1, "first body that is long", config);
        var afterSecond = RejectionHistoryAccumulator.Append(afterFirst, 2, "second body short", config);

        afterSecond.Should().NotContain("first body that is long");
        afterSecond.Should().Contain("## Round 2");
        afterSecond.Should().Contain("second body short");
        System.Text.Encoding.UTF8.GetByteCount(afterSecond).Should().BeLessThanOrEqualTo(32);
    }

    [Fact]
    public void Append_Markdown_SingleEntryExceedsBudget_TruncatesBody()
    {
        // When even the newest single block is too big, fall back to truncating the body so
        // we still record SOMETHING for the round (rather than dropping the only block).
        var config = new RejectionHistoryConfig(Enabled: true, MaxBytes: 24);
        var result = RejectionHistoryAccumulator.Append(
            existing: null,
            round: 1,
            roundBody: new string('x', 1024),
            config: config);

        result.Should().StartWith("## Round 1\n");
        System.Text.Encoding.UTF8.GetByteCount(result).Should().BeLessThanOrEqualTo(24);
    }

    [Fact]
    public void Append_Json_FirstRound_BuildsSingleEntryArray()
    {
        var result = RejectionHistoryAccumulator.Append(
            existing: null,
            round: 1,
            roundBody: "structured feedback",
            config: new RejectionHistoryConfig(Enabled: true, Format: RejectionHistoryFormat.Json));

        var parsed = JsonNode.Parse(result)!.AsArray();
        parsed.Should().HaveCount(1);
        parsed[0]!["round"]!.GetValue<int>().Should().Be(1);
        parsed[0]!["body"]!.GetValue<string>().Should().Be("structured feedback");
    }

    [Fact]
    public void Append_Json_RedeliveredSameRound_OverwritesNotStacks()
    {
        var config = new RejectionHistoryConfig(Enabled: true, Format: RejectionHistoryFormat.Json);
        var first = RejectionHistoryAccumulator.Append(null, 1, "v1", config);
        var second = RejectionHistoryAccumulator.Append(first, 1, "v2", config);

        var parsed = JsonNode.Parse(second)!.AsArray();
        parsed.Should().HaveCount(1);
        parsed[0]!["body"]!.GetValue<string>().Should().Be("v2");
    }

    [Fact]
    public void Append_Json_DropsOldestEntry_OnBudgetExceeded()
    {
        var config = new RejectionHistoryConfig(Enabled: true, MaxBytes: 80, Format: RejectionHistoryFormat.Json);
        var first = RejectionHistoryAccumulator.Append(null, 1, new string('a', 40), config);
        var second = RejectionHistoryAccumulator.Append(first, 2, "b", config);

        var parsed = JsonNode.Parse(second)!.AsArray();
        parsed.Should().HaveCount(1);
        parsed[0]!["round"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void Append_Json_CorruptExisting_StartsFreshNotThrows()
    {
        // Defensive: an unparseable accumulator (manually corrupted, version skew) should
        // not crash the saga — start a clean array and record the new round.
        var config = new RejectionHistoryConfig(Enabled: true, Format: RejectionHistoryFormat.Json);
        var result = RejectionHistoryAccumulator.Append(
            existing: "not-valid-json[",
            round: 7,
            roundBody: "fresh",
            config: config);

        var parsed = JsonNode.Parse(result)!.AsArray();
        parsed.Should().HaveCount(1);
        parsed[0]!["round"]!.GetValue<int>().Should().Be(7);
    }

    [Fact]
    public void Append_HonorsTruncationOnUtf8Boundaries()
    {
        // Multi-byte UTF-8 characters must not be split across the trim boundary.
        var config = new RejectionHistoryConfig(Enabled: true, MaxBytes: 16);
        var result = RejectionHistoryAccumulator.Append(
            existing: null,
            round: 1,
            roundBody: new string('日', 100),
            config: config);

        // Should be valid UTF-8 — round-tripping must not throw.
        var act = () => System.Text.Encoding.UTF8.GetByteCount(result);
        act.Should().NotThrow();
        System.Text.Encoding.UTF8.GetByteCount(result).Should().BeLessThanOrEqualTo(16);
    }
}
