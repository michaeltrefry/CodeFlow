using System.Text.Json;
using Anthropic.Models.Messages;
using CodeFlow.Api.Assistant;
using CodeFlow.Api.Assistant.Tools;
using FluentAssertions;
using OpenAI.Chat;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// CE-1 + CE-2 — verify the assistant emits the expected cache_control breakpoints on the
/// Anthropic side and the cache/reasoning-token breakdown on the OpenAI side.
/// </summary>
public sealed class AssistantCachingTests
{
    [Fact]
    public void BuildSystemBlocks_BothPromptsPresent_StableHeadCarriesEphemeralMarker()
    {
        var blocks = CodeFlowAssistant.BuildSystemBlocks(
            cacheableSystemPrompt: "stable curated body",
            volatileSystemPrompt: "per-turn context");

        blocks.Should().HaveCount(2);
        blocks[0].Text.Should().Be("stable curated body");
        blocks[0].CacheControl.Should().NotBeNull("the cacheable head must carry the ephemeral marker");
        blocks[1].Text.Should().Be("per-turn context");
        blocks[1].CacheControl.Should().BeNull("the volatile tail must not carry a marker — that would shift the cache key per turn");
    }

    [Fact]
    public void BuildSystemBlocks_OnlyCacheable_SingleBlockWithMarker()
    {
        var blocks = CodeFlowAssistant.BuildSystemBlocks(
            cacheableSystemPrompt: "stable",
            volatileSystemPrompt: "");

        blocks.Should().HaveCount(1);
        blocks[0].Text.Should().Be("stable");
        blocks[0].CacheControl.Should().NotBeNull();
    }

    [Fact]
    public void BuildSystemBlocks_OnlyVolatile_NoCacheMarker()
    {
        var blocks = CodeFlowAssistant.BuildSystemBlocks(
            cacheableSystemPrompt: "",
            volatileSystemPrompt: "ephemeral context only");

        blocks.Should().HaveCount(1);
        blocks[0].Text.Should().Be("ephemeral context only");
        blocks[0].CacheControl.Should().BeNull();
    }

    [Fact]
    public void BuildSystemBlocks_BothEmpty_ReturnsEmptyList()
    {
        CodeFlowAssistant.BuildSystemBlocks("", "").Should().BeEmpty();
        CodeFlowAssistant.BuildSystemBlocks("   ", "\n\t").Should().BeEmpty();
    }

    [Fact]
    public void AnthropicToolMapper_MarkLastEphemeral_OnlyLastToolGetsMarker()
    {
        var tools = new IAssistantTool[]
        {
            new StubTool("alpha"),
            new StubTool("beta"),
            new StubTool("gamma"),
        };

        var mapped = AnthropicToolMapper.Map(tools, markLastEphemeral: true);

        mapped.Should().HaveCount(3);
        ((Tool)mapped[0].Value!).CacheControl.Should().BeNull();
        ((Tool)mapped[1].Value!).CacheControl.Should().BeNull();
        ((Tool)mapped[2].Value!).CacheControl.Should().NotBeNull(
            "the marker on the last tool caches the entire tools-array prefix");
    }

    [Fact]
    public void AnthropicToolMapper_MarkLastEphemeralFalse_NoMarkers()
    {
        var tools = new IAssistantTool[] { new StubTool("alpha"), new StubTool("beta") };

        var mapped = AnthropicToolMapper.Map(tools, markLastEphemeral: false);

        mapped.Should().AllSatisfy(t => ((Tool)t.Value!).CacheControl.Should().BeNull());
    }

    [Fact]
    public void AnthropicToolMapper_MarkLastEphemeral_EmptyInput_NoThrow()
    {
        var mapped = AnthropicToolMapper.Map(Array.Empty<IAssistantTool>(), markLastEphemeral: true);
        mapped.Should().BeEmpty();
    }

    [Fact]
    public void SerializeOpenAiUsage_NullInput_ReturnsNull()
    {
        CodeFlowAssistant.SerializeOpenAiUsage(null).Should().BeNull();
    }

    [Fact]
    public void SerializeOpenAiUsage_FullUsage_IncludesCacheBreakdown()
    {
        var usage = OpenAIChatModelFactory.ChatTokenUsage(
            outputTokenCount: 50,
            inputTokenCount: 1234,
            totalTokenCount: 1284,
            outputTokenDetails: OpenAIChatModelFactory.ChatOutputTokenUsageDetails(reasoningTokenCount: 12, audioTokenCount: 0),
            inputTokenDetails: OpenAIChatModelFactory.ChatInputTokenUsageDetails(audioTokenCount: 0, cachedTokenCount: 800));

        var json = CodeFlowAssistant.SerializeOpenAiUsage(usage);

        json.Should().NotBeNull();
        var root = json!.Value;
        root.GetProperty("prompt_tokens").GetInt32().Should().Be(1234);
        root.GetProperty("completion_tokens").GetInt32().Should().Be(50);
        root.GetProperty("total_tokens").GetInt32().Should().Be(1284);
        root.GetProperty("prompt_tokens_details").GetProperty("cached_tokens").GetInt32().Should().Be(800);
        root.GetProperty("prompt_tokens_details").GetProperty("audio_tokens").GetInt32().Should().Be(0);
        root.GetProperty("completion_tokens_details").GetProperty("reasoning_tokens").GetInt32().Should().Be(12);
        root.GetProperty("completion_tokens_details").GetProperty("audio_tokens").GetInt32().Should().Be(0);
    }

    [Fact]
    public void SerializeOpenAiUsage_NoDetails_DefaultsCacheFieldsToZero()
    {
        var usage = OpenAIChatModelFactory.ChatTokenUsage(
            outputTokenCount: 10,
            inputTokenCount: 20,
            totalTokenCount: 30,
            outputTokenDetails: null!);

        var json = CodeFlowAssistant.SerializeOpenAiUsage(usage);

        json.Should().NotBeNull();
        json!.Value.GetProperty("prompt_tokens_details").GetProperty("cached_tokens").GetInt32().Should().Be(0);
        json.Value.GetProperty("completion_tokens_details").GetProperty("reasoning_tokens").GetInt32().Should().Be(0);
    }

    private sealed class StubTool : IAssistantTool
    {
        public StubTool(string name) { Name = name; }
        public string Name { get; }
        public string Description => "stub";
        public JsonElement InputSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement;
        public Task<AssistantToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken)
            => Task.FromResult(new AssistantToolResult("{}", IsError: false));
    }
}
