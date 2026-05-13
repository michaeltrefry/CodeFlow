using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Runtime.Goal;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Goal;

/// <summary>
/// Epic 978 / GN-2 — coverage for the two Goal-scoped meta-tools. The schema-shape tests
/// guard against the kind of strict-validator rejection we hit in
/// <c>feedback_tool_array_needs_items</c> (Anthropic + OpenAI Responses API both reject
/// under-specified shapes), and the <c>goal.update</c> tests pin the enum-of-one contract that
/// keeps the model from inventing pause / abandon / budget-limited statuses.
/// </summary>
public sealed class GoalHostToolProviderTests
{
    [Fact]
    public void Category_IsGoalCategory()
    {
        var provider = new GoalHostToolProvider(new StubState());
        provider.Category.Should().Be(ToolCategory.Goal);
    }

    [Fact]
    public void AvailableTools_ReturnsBothTools()
    {
        var provider = new GoalHostToolProvider(new StubState());
        var tools = provider.AvailableTools(ToolAccessPolicy.AllowAll);

        tools.Select(t => t.Name).Should().BeEquivalentTo(["goal.get", "goal.update"]);
    }

    [Fact]
    public void AvailableTools_RespectsCategoryLimit()
    {
        var provider = new GoalHostToolProvider(new StubState());
        var policy = new ToolAccessPolicy(
            CategoryToolLimits: new Dictionary<ToolCategory, int> { [ToolCategory.Goal] = 0 });

        var tools = provider.AvailableTools(policy);
        tools.Should().BeEmpty();
    }

    [Fact]
    public void UpdateGoal_Schema_AcceptsCompleteAndAbandonEnum()
    {
        var spec = GoalHostToolProvider.GetCatalog().Single(t => t.Name == "goal.update");
        var statusEnum = spec.Parameters!["properties"]!["status"]!["enum"]!.AsArray();

        statusEnum.Select(n => n!.GetValue<string>())
            .Should().BeEquivalentTo(["complete", "abandon"]);

        // Only `status` is in the top-level required list. `reason` is enforced at the handler
        // layer when status=="abandon", not at schema-level, because OpenAI/Anthropic don't
        // express conditional-required cross-field rules in JSON Schema.
        spec.Parameters!["required"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .Should().BeEquivalentTo(["status"]);

        // `reason` must be declared in `properties` so models can populate it.
        spec.Parameters!["properties"]!["reason"]!["type"]!.GetValue<string>().Should().Be("string");

        spec.IsMutating.Should().BeTrue();
    }

    [Fact]
    public void GetGoal_Schema_DeclaresEmptyPropertiesObject()
    {
        // Strict validators reject `{"type":"object"}` without an explicit (possibly empty)
        // `properties` object. The verbatim-port from Codex goal_spec.rs `create_get_goal_tool`
        // ships an empty properties map; mirror that here so neither Anthropic nor OpenAI
        // Responses-API rejects on tool registration.
        var spec = GoalHostToolProvider.GetCatalog().Single(t => t.Name == "goal.get");
        spec.Parameters!["type"]!.GetValue<string>().Should().Be("object");
        spec.Parameters!["properties"].Should().NotBeNull();
        spec.Parameters!["properties"]!.AsObject().Count.Should().Be(0);
        spec.Parameters!["additionalProperties"]!.GetValue<bool>().Should().BeFalse();
        spec.IsMutating.Should().BeFalse();
    }

    [Fact]
    public async Task GetGoal_ReturnsCurrentSnapshotAsJson()
    {
        var state = new StubState(new GoalRuntimeStateSnapshot(
            Objective: "Ship sc-981",
            TokenBudget: 500_000,
            TokensUsed: 12_345,
            TokensRemaining: 487_655,
            IsCompleteRequested: false));
        var provider = new GoalHostToolProvider(state);

        var result = await provider.InvokeAsync(new ToolCall("call_get", "goal.get", new JsonObject()));

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("objective").GetString().Should().Be("Ship sc-981");
        doc.RootElement.GetProperty("tokenBudget").GetInt32().Should().Be(500_000);
        doc.RootElement.GetProperty("tokensUsed").GetInt32().Should().Be(12_345);
        doc.RootElement.GetProperty("tokensRemaining").GetInt32().Should().Be(487_655);
    }

    [Fact]
    public async Task GetGoal_UnboundedRun_EmitsNullBudgetAndRemaining()
    {
        var state = new StubState(new GoalRuntimeStateSnapshot(
            Objective: "Open-ended exploration",
            TokenBudget: null,
            TokensUsed: 800,
            TokensRemaining: null,
            IsCompleteRequested: false));
        var provider = new GoalHostToolProvider(state);

        var result = await provider.InvokeAsync(new ToolCall("call_get", "goal.get", new JsonObject()));

        using var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("tokenBudget").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("tokensRemaining").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task UpdateGoal_CompleteStatus_MarksRuntimeComplete()
    {
        var state = new StubState();
        var provider = new GoalHostToolProvider(state);

        var result = await provider.InvokeAsync(new ToolCall(
            "call_update",
            "goal.update",
            new JsonObject { ["status"] = "complete" }));

        result.IsError.Should().BeFalse();
        state.MarkCompleteCalls.Should().Be(1);
        using var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("acknowledged").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("status").GetString().Should().Be("complete");
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("abandoned")]    // past tense — only the verb "abandon" is accepted
    [InlineData("blocked")]
    [InlineData("budget-limited")]
    [InlineData("failed")]
    [InlineData("COMPLETE")]     // case sensitive — only lowercase "complete"
    [InlineData("ABANDON")]      // case sensitive — only lowercase "abandon"
    public async Task UpdateGoal_NonAcceptedStatus_ReturnsError(string badStatus)
    {
        var state = new StubState();
        var provider = new GoalHostToolProvider(state);

        var result = await provider.InvokeAsync(new ToolCall(
            "call_update",
            "goal.update",
            new JsonObject { ["status"] = badStatus }));

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("complete");
        result.Content.Should().Contain("abandon");
        state.MarkCompleteCalls.Should().Be(0);
        state.MarkAbandonedCalls.Should().Be(0);
    }

    [Fact]
    public async Task UpdateGoal_AbandonWithReason_CallsMarkAbandoned()
    {
        var state = new StubState();
        var provider = new GoalHostToolProvider(state);

        var reason = "container.run consistently rejects every legitimate approach with "
            + "workspace_invalid; Python is unreachable in this environment.";
        var result = await provider.InvokeAsync(new ToolCall(
            "call_update",
            "goal.update",
            new JsonObject { ["status"] = "abandon", ["reason"] = reason }));

        result.IsError.Should().BeFalse();
        state.MarkAbandonedCalls.Should().Be(1);
        state.LastAbandonReason.Should().Be(reason);
        state.MarkCompleteCalls.Should().Be(0);

        using var doc = JsonDocument.Parse(result.Content);
        doc.RootElement.GetProperty("acknowledged").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("status").GetString().Should().Be("abandon");
        doc.RootElement.GetProperty("reason").GetString().Should().Be(reason);
    }

    [Fact]
    public async Task UpdateGoal_AbandonWithoutReason_ReturnsError()
    {
        var state = new StubState();
        var provider = new GoalHostToolProvider(state);

        var result = await provider.InvokeAsync(new ToolCall(
            "call_update",
            "goal.update",
            new JsonObject { ["status"] = "abandon" }));

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("reason");
        state.MarkAbandonedCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n  \r")]
    public async Task UpdateGoal_AbandonWithBlankReason_ReturnsError(string blankReason)
    {
        var state = new StubState();
        var provider = new GoalHostToolProvider(state);

        var result = await provider.InvokeAsync(new ToolCall(
            "call_update",
            "goal.update",
            new JsonObject { ["status"] = "abandon", ["reason"] = blankReason }));

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("reason");
        state.MarkAbandonedCalls.Should().Be(0);
    }

    [Fact]
    public async Task UpdateGoal_AbandonWithNonStringReason_ReturnsError()
    {
        var state = new StubState();
        var provider = new GoalHostToolProvider(state);

        var result = await provider.InvokeAsync(new ToolCall(
            "call_update",
            "goal.update",
            new JsonObject { ["status"] = "abandon", ["reason"] = 42 }));

        result.IsError.Should().BeTrue();
        state.MarkAbandonedCalls.Should().Be(0);
    }

    [Fact]
    public async Task UpdateGoal_MissingStatusField_ReturnsError()
    {
        var state = new StubState();
        var provider = new GoalHostToolProvider(state);

        var result = await provider.InvokeAsync(new ToolCall(
            "call_update",
            "goal.update",
            new JsonObject()));

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("status");
        state.MarkCompleteCalls.Should().Be(0);
    }

    [Fact]
    public async Task UpdateGoal_NonStringStatus_ReturnsError()
    {
        var state = new StubState();
        var provider = new GoalHostToolProvider(state);

        var result = await provider.InvokeAsync(new ToolCall(
            "call_update",
            "goal.update",
            new JsonObject { ["status"] = 1 }));

        result.IsError.Should().BeTrue();
        state.MarkCompleteCalls.Should().Be(0);
    }

    [Fact]
    public async Task UpdateGoal_NonObjectArguments_ReturnsError()
    {
        var state = new StubState();
        var provider = new GoalHostToolProvider(state);

        var result = await provider.InvokeAsync(new ToolCall(
            "call_update",
            "goal.update",
            JsonValue.Create("complete")));

        result.IsError.Should().BeTrue();
        state.MarkCompleteCalls.Should().Be(0);
    }

    [Fact]
    public async Task UnknownToolName_Throws()
    {
        var provider = new GoalHostToolProvider(new StubState());
        await Assert.ThrowsAsync<UnknownToolException>(() =>
            provider.InvokeAsync(new ToolCall("call_x", "goal.unknown", new JsonObject())));
    }

    [Fact]
    public void BuildProviders_OmitsGoalProvider_WhenGoalStateIsNull()
    {
        // The provider's gating contract: an agent invocation without a GoalState in its
        // configuration must NOT see the goal.* tools. This ensures the homepage assistant
        // (which never sets GoalState) and other workflow node kinds (Agent/Hitl/etc.) cannot
        // accidentally surface the tools.
        var registry = new ToolRegistry([], refusalSink: null, nowProvider: null);
        registry.AvailableTools().Should().BeEmpty();

        // A registry constructed without GoalHostToolProvider has no goal.* tools.
        registry.AvailableTools().Should().NotContain(t => t.Name.StartsWith("goal."));
    }

    [Fact]
    public void Registry_WithGoalProvider_ExposesGoalTools()
    {
        var registry = new ToolRegistry(
            [new GoalHostToolProvider(new StubState())],
            refusalSink: null,
            nowProvider: null);

        var names = registry.AvailableTools().Select(t => t.Name);
        names.Should().Contain(["goal.get", "goal.update"]);
    }

    [Fact]
    public void Ctor_NullState_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new GoalHostToolProvider(null!));
    }

    private sealed class StubState : IGoalRuntimeState
    {
        private readonly GoalRuntimeStateSnapshot snapshot;
        public int MarkCompleteCalls { get; private set; }
        public int MarkAbandonedCalls { get; private set; }
        public string? LastAbandonReason { get; private set; }

        public StubState() : this(new GoalRuntimeStateSnapshot(
            Objective: "default objective",
            TokenBudget: 100_000,
            TokensUsed: 0,
            TokensRemaining: 100_000,
            IsCompleteRequested: false))
        {
        }

        public StubState(GoalRuntimeStateSnapshot snapshot)
        {
            this.snapshot = snapshot;
        }

        public GoalRuntimeStateSnapshot Snapshot() => snapshot;

        public void MarkComplete() => MarkCompleteCalls++;

        public void MarkAbandoned(string reason)
        {
            MarkAbandonedCalls++;
            LastAbandonReason = reason;
        }
    }
}
