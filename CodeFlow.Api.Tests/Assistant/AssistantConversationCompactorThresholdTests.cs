using CodeFlow.Api.Assistant;
using CodeFlow.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Unit tests for the pure-logic threshold gate on <see cref="AssistantConversationCompactor"/>.
/// The full <c>CompactAsync</c> path requires a live LLM provider so the trigger condition is
/// tested in isolation here; SDK round-trips are covered in the persistence-level repository
/// integration tests where the database side-effects are observable.
/// </summary>
public sealed class AssistantConversationCompactorThresholdTests
{
    [Fact]
    public void ShouldCompact_NoCap_ReturnsFalse()
    {
        var compactor = BuildCompactor();
        var conversation = MakeConversation(inputTotal: 999_999, outputTotal: 999_999);

        compactor.ShouldCompact(conversation, maxTokensPerConversation: null).Should().BeFalse();
        compactor.ShouldCompact(conversation, maxTokensPerConversation: 0).Should().BeFalse();
    }

    [Fact]
    public void ShouldCompact_BelowThreshold_ReturnsFalse()
    {
        var compactor = BuildCompactor();
        // 94% of 1000 = 940 — below the 95% trigger, so no compaction.
        var conversation = MakeConversation(inputTotal: 500, outputTotal: 440);

        compactor.ShouldCompact(conversation, maxTokensPerConversation: 1000).Should().BeFalse();
    }

    [Fact]
    public void ShouldCompact_AtThreshold_ReturnsTrue()
    {
        var compactor = BuildCompactor();
        // 95% of 1000 = 950 — exactly on the trigger.
        var conversation = MakeConversation(inputTotal: 500, outputTotal: 450);

        compactor.ShouldCompact(conversation, maxTokensPerConversation: 1000).Should().BeTrue();
    }

    [Fact]
    public void ShouldCompact_AboveThreshold_ReturnsTrue()
    {
        var compactor = BuildCompactor();
        var conversation = MakeConversation(inputTotal: 800, outputTotal: 200);

        compactor.ShouldCompact(conversation, maxTokensPerConversation: 1000).Should().BeTrue();
    }

    [Fact]
    public void ShouldCompact_RoundsUpForFractionalThreshold()
    {
        var compactor = BuildCompactor();
        // 95% of 17 = 16.15 → ceil to 17. Anything below 17 must NOT trigger.
        var conversation16 = MakeConversation(inputTotal: 8, outputTotal: 8);
        compactor.ShouldCompact(conversation16, maxTokensPerConversation: 17).Should().BeFalse();

        var conversation17 = MakeConversation(inputTotal: 9, outputTotal: 8);
        compactor.ShouldCompact(conversation17, maxTokensPerConversation: 17).Should().BeTrue();
    }

    private static AssistantConversation MakeConversation(long inputTotal, long outputTotal) => new(
        Id: Guid.NewGuid(),
        UserId: "user",
        ScopeKind: AssistantConversationScopeKind.Homepage,
        EntityType: null,
        EntityId: null,
        ScopeKey: "homepage",
        SyntheticTraceId: Guid.NewGuid(),
        InputTokensTotal: inputTotal,
        OutputTokensTotal: outputTotal,
        ActiveWorkspaceSignature: null,
        CompactedThroughSequence: 0,
        CreatedAtUtc: DateTime.UtcNow,
        UpdatedAtUtc: DateTime.UtcNow);

    private static AssistantConversationCompactor BuildCompactor() => new(
        conversations: null!,
        settingsResolver: null!,
        providerSettings: null!,
        anthropicClient: null!,
        logger: NullLogger<AssistantConversationCompactor>.Instance);
}
