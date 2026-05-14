using FluentAssertions;

namespace CodeFlow.Runtime.Tests;

public sealed class ResolvedAgentToolsTests
{
    private static McpToolDefinition Mcp(string server, string tool, bool mutating = false) =>
        new(server, tool, $"{server}/{tool}", Parameters: null, IsMutating: mutating);

    [Fact]
    public void Merge_unions_allowed_tool_names()
    {
        var a = new ResolvedAgentTools(new[] { "echo" }, Array.Empty<McpToolDefinition>(), EnableHostTools: true);
        var b = new ResolvedAgentTools(new[] { "now" }, Array.Empty<McpToolDefinition>(), EnableHostTools: true);

        var merged = a.Merge(b);

        merged.AllowedToolNames.Should().BeEquivalentTo("echo", "now");
    }

    [Fact]
    public void Merge_dedupes_allowed_tool_names_case_insensitively()
    {
        var a = new ResolvedAgentTools(new[] { "echo" }, Array.Empty<McpToolDefinition>(), EnableHostTools: true);
        var b = new ResolvedAgentTools(new[] { "ECHO", "now" }, Array.Empty<McpToolDefinition>(), EnableHostTools: true);

        var merged = a.Merge(b);

        merged.AllowedToolNames.Should().HaveCount(2);
        merged.AllowedToolNames.Should().Contain("now");
    }

    [Fact]
    public void Merge_unions_mcp_tools_deduped_by_full_name()
    {
        var shared = Mcp("svc", "read");
        var a = new ResolvedAgentTools(
            Array.Empty<string>(),
            new[] { shared },
            EnableHostTools: false);
        var b = new ResolvedAgentTools(
            Array.Empty<string>(),
            new[] { shared, Mcp("svc", "write", mutating: true) },
            EnableHostTools: false);

        var merged = a.Merge(b);

        merged.McpTools.Select(t => t.FullName)
            .Should().BeEquivalentTo("mcp:svc:read", "mcp:svc:write");
        merged.McpTools.Single(t => t.ToolName == "write").IsMutating.Should().BeTrue();
    }

    [Fact]
    public void Merge_ors_enable_host_tools()
    {
        var hostOff = new ResolvedAgentTools(
            Array.Empty<string>(), Array.Empty<McpToolDefinition>(), EnableHostTools: false);
        var hostOn = new ResolvedAgentTools(
            new[] { "echo" }, Array.Empty<McpToolDefinition>(), EnableHostTools: true);

        hostOff.Merge(hostOn).EnableHostTools.Should().BeTrue();
        hostOff.Merge(hostOff).EnableHostTools.Should().BeFalse();
    }

    [Fact]
    public void Merge_unions_granted_skills_deduped_by_name()
    {
        var shared = new ResolvedSkill("alpha", "body-a");
        var a = new ResolvedAgentTools(
            Array.Empty<string>(), Array.Empty<McpToolDefinition>(), EnableHostTools: false,
            new[] { shared });
        var b = new ResolvedAgentTools(
            Array.Empty<string>(), Array.Empty<McpToolDefinition>(), EnableHostTools: false,
            new[] { shared, new ResolvedSkill("bravo", "body-b") });

        var merged = a.Merge(b);

        merged.GrantedSkills.Select(s => s.Name).Should().BeEquivalentTo("alpha", "bravo");
    }

    [Fact]
    public void Merge_with_Empty_preserves_the_other_side()
    {
        var tools = new ResolvedAgentTools(
            new[] { "echo" },
            new[] { Mcp("svc", "read") },
            EnableHostTools: true,
            new[] { new ResolvedSkill("alpha", "body") });

        var merged = ResolvedAgentTools.Empty.Merge(tools);

        merged.AllowedToolNames.Should().BeEquivalentTo("echo");
        merged.McpTools.Should().ContainSingle();
        merged.EnableHostTools.Should().BeTrue();
        merged.GrantedSkills.Should().ContainSingle();
    }
}
