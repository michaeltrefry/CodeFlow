using FluentAssertions;
using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.Tests;

public sealed class ToolRegistryTests
{
    [Fact]
    public void AvailableTools_ShouldComposeProvidersAndApplyAllowlistIntersection()
    {
        var registry = new ToolRegistry(
        [
            new HostToolProvider(static () => DateTimeOffset.Parse("2026-04-20T01:23:00Z")),
            new FakeToolProvider(
                ToolCategory.Mcp,
                [new ToolSchema("search_docs", "Searches indexed docs.", new JsonObject())])
        ]);

        var tools = registry.AvailableTools(new ToolAccessPolicy(
            AllowedToolNames: ["now", "search_docs"]));

        tools.Select(tool => tool.Name).Should().Equal("now", "search_docs");
    }

    [Fact]
    public void AvailableTools_ShouldApplyCategoryFiltering()
    {
        var registry = new ToolRegistry([new HostToolProvider()]);

        var tools = registry.AvailableTools(new ToolAccessPolicy(
            CategoryToolLimits: new Dictionary<ToolCategory, int>
            {
                [ToolCategory.Host] = 1
            }));

        tools.Should().ContainSingle();
        tools[0].Name.Should().Be("echo");
    }

    [Fact]
    public async Task InvokeAsync_ShouldDispatchToOwningProvider()
    {
        var provider = new FakeToolProvider(
            ToolCategory.Execution,
            [new ToolSchema("run_command", "Runs a command.", new JsonObject())]);
        var registry = new ToolRegistry([provider]);

        var result = await registry.InvokeAsync(
            new ToolCall("call_123", "run_command", new JsonObject { ["command"] = "pwd" }),
            AgentInvocationContext.ForTests());

        result.Should().BeEquivalentTo(new ToolResult("call_123", "handled:run_command"));
        provider.InvokedToolNames.Should().ContainSingle().Which.Should().Be("run_command");
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturnHostToolResults()
    {
        var registry = new ToolRegistry(
        [
            new HostToolProvider(static () => DateTimeOffset.Parse("2026-04-20T01:24:25Z"))
        ]);

        var echoResult = await registry.InvokeAsync(
            new ToolCall("call_echo", "echo", new JsonObject { ["text"] = "hello" }),
            AgentInvocationContext.ForTests());
        var nowResult = await registry.InvokeAsync(
            new ToolCall("call_now", "now", new JsonObject()),
            AgentInvocationContext.ForTests());

        echoResult.Should().BeEquivalentTo(new ToolResult("call_echo", "hello"));
        nowResult.Should().BeEquivalentTo(new ToolResult("call_now", "2026-04-20T01:24:25.0000000+00:00"));
    }

    [Fact]
    public async Task InvokeAsync_ShouldThrowUnknownToolException_WhenToolIsUnavailable()
    {
        var registry = new ToolRegistry([new HostToolProvider()]);

        var act = async () => await registry.InvokeAsync(
            new ToolCall("call_missing", "missing_tool", new JsonObject()),
            AgentInvocationContext.ForTests());

        var exception = await act.Should().ThrowAsync<UnknownToolException>();
        exception.Which.ToolName.Should().Be("missing_tool");
    }

    private sealed class FakeToolProvider(ToolCategory category, IReadOnlyList<ToolSchema> tools) : IToolProvider
    {
        public List<string> InvokedToolNames { get; } = [];

        public ToolCategory Category { get; } = category;

        public IReadOnlyList<ToolSchema> AvailableTools(ToolAccessPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);

            return tools
                .Take(policy.GetCategoryLimit(Category))
                .ToArray();
        }

        public Task<ToolResult> InvokeAsync(
            ToolCall toolCall,
            AgentInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            InvokedToolNames.Add(toolCall.Name);
            return Task.FromResult(new ToolResult(toolCall.Id, $"handled:{toolCall.Name}"));
        }
    }
}
