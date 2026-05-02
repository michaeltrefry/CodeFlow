using CodeFlow.Api.Assistant;
using CodeFlow.Api.Assistant.Skills;
using CodeFlow.Api.Assistant.Tools;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Unit tests for AS-6's <see cref="CodeFlowAssistant.BuildAnthropicToolResultBlock"/>. Targets
/// the cache-control decision in isolation: a successful <c>load_assistant_skill</c> result must
/// carry <c>cache_control=ephemeral</c> so Anthropic anchors a prompt-cache prefix on the skill
/// body; every other tool result — including errored skill loads — must NOT carry the marker.
/// </summary>
public sealed class AnthropicToolResultBlockBuilderTests
{
    private const string SuccessJson = """{"key":"workflow-authoring","body":"…"}""";
    private const string ErrorJson = """{"error":"No assistant skill with key 'nope'."}""";

    [Fact]
    public void SuccessfulSkillLoad_MarksBlockWithEphemeralCacheControl()
    {
        var block = CodeFlowAssistant.BuildAnthropicToolResultBlock(
            toolUseId: "tu-skill-1",
            toolName: LoadAssistantSkillTool.ToolName,
            result: new AssistantToolResult(SuccessJson));

        block.ToolUseID.Should().Be("tu-skill-1");
        block.IsError.Should().BeNull();
        block.CacheControl.Should().NotBeNull(
            because: "the skill body is content-stable across the rest of the session and worth a cache prefix");
    }

    [Fact]
    public void ErroredSkillLoad_DoesNotMarkBlock()
    {
        var block = CodeFlowAssistant.BuildAnthropicToolResultBlock(
            toolUseId: "tu-skill-err",
            toolName: LoadAssistantSkillTool.ToolName,
            result: new AssistantToolResult(ErrorJson, IsError: true));

        block.IsError.Should().BeTrue();
        block.CacheControl.Should().BeNull(
            because: "errored skill loads are not content-stable; caching them would waste a breakpoint");
    }

    [Theory]
    // Sample of other tools the assistant might dispatch — none should be marked. The cache
    // breakpoint pool is small (Anthropic allows up to four per request) and these results are
    // either small enough not to need caching (registry tools) or content-volatile across turns
    // (workflow-package fetches that get demoted by the redaction tracker).
    [InlineData("list_assistant_skills")]
    [InlineData("list_workflows")]
    [InlineData("get_workflow")]
    [InlineData("save_workflow_package")]
    [InlineData("get_workflow_package_draft")]
    public void OtherTools_DoNotMarkBlock(string toolName)
    {
        var block = CodeFlowAssistant.BuildAnthropicToolResultBlock(
            toolUseId: "tu-other",
            toolName: toolName,
            result: new AssistantToolResult("""{"status":"ok"}"""));

        block.CacheControl.Should().BeNull(
            because: $"only `load_assistant_skill` results are marked; '{toolName}' must not consume a cache breakpoint");
    }

    [Fact]
    public void ListAssistantSkillsTool_HasStableNameConstant()
    {
        // Sanity check — the Anthropic builder relies on the const string matching the runtime
        // tool name. A future rename that updates one but not the other would silently disable
        // the cache marker.
        LoadAssistantSkillTool.ToolName.Should().Be("load_assistant_skill");
        new LoadAssistantSkillTool(new EmbeddedAssistantSkillProvider()).Name
            .Should().Be(LoadAssistantSkillTool.ToolName);
    }
}
