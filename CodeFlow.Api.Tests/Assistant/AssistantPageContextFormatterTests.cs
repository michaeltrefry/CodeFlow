using CodeFlow.Api.Assistant;
using FluentAssertions;

namespace CodeFlow.Api.Tests.Assistant;

/// <summary>
/// Unit tests for <see cref="AssistantPageContextFormatter"/> — the surface that turns the
/// client-supplied <see cref="AssistantPageContext"/> into a system-message snippet the model
/// reads on each turn (HAA-8).
/// </summary>
public sealed class AssistantPageContextFormatterTests
{
    [Fact]
    public void FormatAsSystemMessage_NullContext_ReturnsNull()
    {
        AssistantPageContextFormatter.FormatAsSystemMessage(null).Should().BeNull();
    }

    [Fact]
    public void FormatAsSystemMessage_BlankKind_ReturnsNull()
    {
        var ctx = new AssistantPageContext(Kind: "", null, null, null, null, null);
        AssistantPageContextFormatter.FormatAsSystemMessage(ctx).Should().BeNull();
    }

    [Fact]
    public void FormatAsSystemMessage_TraceWithNode_RendersAllFieldsAndHint()
    {
        var ctx = new AssistantPageContext(
            Kind: "trace",
            Route: "/traces/abc",
            EntityType: "trace",
            EntityId: "abc",
            SelectedNodeId: "node-3",
            SelectedScriptSlot: null);

        var msg = AssistantPageContextFormatter.FormatAsSystemMessage(ctx);

        msg.Should().NotBeNull();
        msg.Should().StartWith("<current-page-context>")
            .And.EndWith("</current-page-context>")
            .And.Contain("Kind: trace")
            .And.Contain("Route: /traces/abc")
            .And.Contain("Entity: trace=abc")
            .And.Contain("Selected node: node-3")
            .And.Contain("\"This trace\" refers to the entity above")
            .And.NotContain("Selected script slot");
    }

    [Fact]
    public void FormatAsSystemMessage_WorkflowEditorWithSlot_RendersSlot()
    {
        var ctx = new AssistantPageContext(
            Kind: "workflow-editor",
            Route: "/workflows/foo/edit",
            EntityType: "workflow",
            EntityId: "foo",
            SelectedNodeId: "n1",
            SelectedScriptSlot: "input");

        var msg = AssistantPageContextFormatter.FormatAsSystemMessage(ctx);

        msg.Should().Contain("Selected node: n1")
            .And.Contain("Selected script slot: input")
            .And.Contain("\"This node\" refers to the selected node");
    }

    [Fact]
    public void FormatAsSystemMessage_HomeKind_OmitsEntityFields()
    {
        var ctx = new AssistantPageContext(
            Kind: "home",
            Route: "/",
            EntityType: null,
            EntityId: null,
            SelectedNodeId: null,
            SelectedScriptSlot: null);

        var msg = AssistantPageContextFormatter.FormatAsSystemMessage(ctx);

        msg.Should().Contain("Kind: home")
            .And.Contain("Route: /")
            .And.NotContain("Entity:")
            .And.NotContain("Selected node:")
            .And.Contain("home page");
    }

    [Fact]
    public void FormatAsSystemMessage_OtherKind_HasNoHintBlock()
    {
        var ctx = new AssistantPageContext(
            Kind: "other",
            Route: "/settings/skills",
            EntityType: null,
            EntityId: null,
            SelectedNodeId: null,
            SelectedScriptSlot: null);

        var msg = AssistantPageContextFormatter.FormatAsSystemMessage(ctx);

        msg.Should().Contain("Kind: other")
            .And.Contain("Route: /settings/skills");
    }
}
