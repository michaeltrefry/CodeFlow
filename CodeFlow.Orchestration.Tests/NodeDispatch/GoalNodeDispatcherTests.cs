using System.Text.Json;
using CodeFlow.Orchestration.NodeDispatch;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Goal;
using FluentAssertions;

namespace CodeFlow.Orchestration.Tests.NodeDispatch;

/// <summary>
/// Epic 978 / GN-3b — unit coverage for the pure-function helpers inside
/// <see cref="GoalNodeDispatcher"/>. The wiring path (DispatchAsync) needs the saga-bus
/// harness for end-to-end coverage; that lives alongside the existing
/// <c>WorkflowSagaSwarmEndToEndTests</c> pattern and lands in a follow-up. These tests pin
/// the helpers in isolation so a future refactor of outcome routing or objective rendering
/// has explicit guardrails.
/// </summary>
public sealed class GoalNodeDispatcherTests
{
    private static readonly IScribanTemplateRenderer Renderer = new ScribanTemplateRenderer(
        renderTimeout: TimeSpan.FromSeconds(2));

    [Fact]
    public void RenderGoalObjective_PlainText_PassesThrough()
    {
        var rendered = GoalNodeDispatcher.RenderGoalObjective(
            Renderer,
            "Complete the story acceptance criteria",
            contextInputs: new Dictionary<string, JsonElement>(),
            workflowInputs: new Dictionary<string, JsonElement>());

        rendered.Should().Be("Complete the story acceptance criteria");
    }

    [Fact]
    public void RenderGoalObjective_SubstitutesWorkflowVariables()
    {
        var workflow = new Dictionary<string, JsonElement>
        {
            ["story_id"] = JsonDocument.Parse("\"sc-989\"").RootElement,
            ["story_title"] = JsonDocument.Parse("\"Wire the dispatcher\"").RootElement,
        };

        var rendered = GoalNodeDispatcher.RenderGoalObjective(
            Renderer,
            "Complete story {{ workflow.story_id }}: {{ workflow.story_title }}",
            contextInputs: new Dictionary<string, JsonElement>(),
            workflowInputs: workflow);

        rendered.Should().Be("Complete story sc-989: Wire the dispatcher");
    }

    [Fact]
    public void RenderGoalObjective_SubstitutesContextVariables()
    {
        var context = new Dictionary<string, JsonElement>
        {
            ["focus"] = JsonDocument.Parse("\"the audit checklist\"").RootElement,
        };

        var rendered = GoalNodeDispatcher.RenderGoalObjective(
            Renderer,
            "Work on {{ context.focus }}",
            contextInputs: context,
            workflowInputs: new Dictionary<string, JsonElement>());

        rendered.Should().Be("Work on the audit checklist");
    }

    [Fact]
    public void RenderGoalObjective_PrefersWorkflowAndContextNamespaces()
    {
        // Pin the variable scope so a future change doesn't accidentally expose budget /
        // input / loop variables that AgentPromptScopeBuilder.Merge would normally surface to
        // an agent's prompt template. The objective renders in a NARROWER scope — only the
        // saga-state bags (workflow.*, context.*) — so authors can't reference render-time
        // budget vars in the goal text.
        var rendered = GoalNodeDispatcher.RenderGoalObjective(
            Renderer,
            "{{ if workflow.x }}has-x{{ else }}no-x{{ end }} :: {{ if context.y }}has-y{{ else }}no-y{{ end }}",
            contextInputs: new Dictionary<string, JsonElement>
            {
                ["y"] = JsonDocument.Parse("\"present\"").RootElement,
            },
            workflowInputs: new Dictionary<string, JsonElement>());

        rendered.Should().Be("no-x :: has-y");
    }

    [Fact]
    public void RenderGoalObjective_HandlesNestedJsonObjects()
    {
        var workflow = new Dictionary<string, JsonElement>
        {
            ["story"] = JsonDocument.Parse("""{ "id": "sc-989", "url": "https://example.com/sc-989" }""").RootElement,
        };

        var rendered = GoalNodeDispatcher.RenderGoalObjective(
            Renderer,
            "{{ workflow.story.id }} at {{ workflow.story.url }}",
            contextInputs: new Dictionary<string, JsonElement>(),
            workflowInputs: workflow);

        rendered.Should().Be("sc-989 at https://example.com/sc-989");
    }

    [Theory]
    [InlineData(GoalIterationOutcome.Success, "Success")]
    [InlineData(GoalIterationOutcome.BudgetLimited, "BudgetLimited")]
    [InlineData(GoalIterationOutcome.IterationCapReached, "Failed")]
    public void MapOutcome_RoutesEachOutcomeToTheRightPort(GoalIterationOutcome outcome, string expectedPort)
    {
        var result = BuildResult(outcome, finalOutput: "the work product");

        var (port, output) = GoalNodeDispatcher.MapOutcome(result);

        port.Should().Be(expectedPort);
        output.Should().Be("the work product");
    }

    [Fact]
    public void MapOutcome_EmptyIterations_ReturnsEmptyOutput()
    {
        // Defensive: orchestrator should always emit at least one iteration record, but if it
        // somehow doesn't (cancellation between iteration N start and result construction),
        // the dispatcher must still produce a publishable completion.
        var result = new GoalIterationResult(
            Outcome: GoalIterationOutcome.IterationCapReached,
            Iterations: Array.Empty<GoalIterationRecord>(),
            FinalHistory: Array.Empty<ChatMessage>(),
            FinalSnapshot: new GoalRuntimeStateSnapshot("o", null, 0, null, false),
            TotalInputTokens: 0,
            TotalOutputTokens: 0,
            TotalToolCallsExecuted: 0);

        var (port, output) = GoalNodeDispatcher.MapOutcome(result);

        port.Should().Be("Failed");
        output.Should().BeEmpty();
    }

    [Fact]
    public void BuildDecisionPayload_IncludesOutcomeAndCounters()
    {
        var result = new GoalIterationResult(
            Outcome: GoalIterationOutcome.Success,
            Iterations:
            [
                new GoalIterationRecord(1, "draft", 0, null, new AgentDecision("Completed")),
                new GoalIterationRecord(2, "final", 0, null, new AgentDecision("Completed")),
            ],
            FinalHistory: Array.Empty<ChatMessage>(),
            FinalSnapshot: new GoalRuntimeStateSnapshot("obj", 100_000, 12_500, 87_500, IsCompleteRequested: true),
            TotalInputTokens: 10_000,
            TotalOutputTokens: 2_500,
            TotalToolCallsExecuted: 3);

        var payload = GoalNodeDispatcher.BuildDecisionPayload(result)!.Value;

        payload.GetProperty("outcome").GetString().Should().Be("Success");
        payload.GetProperty("iterationCount").GetInt32().Should().Be(2);
        payload.GetProperty("tokensUsed").GetInt32().Should().Be(12_500);
        payload.GetProperty("tokenBudget").GetInt32().Should().Be(100_000);
        payload.GetProperty("isCompleteRequested").GetBoolean().Should().BeTrue();
        payload.TryGetProperty("reason", out _).Should().BeFalse(
            "the Success outcome does not need a reason hint — only IterationCapReached does");
    }

    [Fact]
    public void BuildDecisionPayload_IterationCapReached_IncludesReason()
    {
        // The saga's retry-context builder and trace inspectors read `reason` to surface a
        // human-readable failure description; pin its value for the cap path.
        var result = BuildResult(GoalIterationOutcome.IterationCapReached, "stuck");

        var payload = GoalNodeDispatcher.BuildDecisionPayload(result)!.Value;

        payload.GetProperty("reason").GetString().Should().Be("GoalIterationCapReached");
    }

    [Fact]
    public void BuildGoalToolExecutionContext_NoTraceWorkDir_ReturnsNull()
    {
        // Goal nodes only run in code-aware workflows (sc-593 lifts workflow.workDir to
        // saga.TraceWorkDir at start). A null/blank field means the saga never had a
        // workspace anchor, which we treat as "no tool execution context."
        var saga = new WorkflowSagaStateEntity { TraceWorkDir = null };

        var result = GoalNodeDispatcher.BuildGoalToolExecutionContext(saga);

        result.Should().BeNull();
    }

    [Fact]
    public void BuildGoalToolExecutionContext_WithTraceWorkDir_BuildsWorkspaceContext()
    {
        var traceId = Guid.NewGuid();
        var rootTraceId = Guid.NewGuid();
        var traceWorkDir = $"/app/codeflow/workdir/{rootTraceId:N}";

        var saga = new WorkflowSagaStateEntity
        {
            TraceId = traceId,
            TraceWorkDir = traceWorkDir,
            RepositoriesJson = null,
        };

        var result = GoalNodeDispatcher.BuildGoalToolExecutionContext(saga);

        result.Should().NotBeNull();
        result!.Workspace.Should().NotBeNull();
        result.Workspace!.CorrelationId.Should().Be(traceId);
        result.Workspace.RootPath.Should().Be(traceWorkDir);
        result.Workspace.RootTraceId.Should().Be(rootTraceId);
        result.Repositories.Should().BeNull();
        result.Envelope.Should().BeNull("envelope resolution is deferred for the v1 Goal dispatcher");
    }

    [Fact]
    public void BuildGoalToolExecutionContext_WithRepositories_PopulatesRepositoryContexts()
    {
        var traceId = Guid.NewGuid();
        var rootTraceId = Guid.NewGuid();
        var saga = new WorkflowSagaStateEntity
        {
            TraceId = traceId,
            TraceWorkDir = $"/app/codeflow/workdir/{rootTraceId:N}",
            RepositoriesJson = """[{ "url": "https://github.com/michaeltrefry/CodeFlow.git" }]""",
        };

        var result = GoalNodeDispatcher.BuildGoalToolExecutionContext(saga);

        result.Should().NotBeNull();
        result!.Repositories.Should().NotBeNull().And.HaveCount(1);
        var repo = result.Repositories![0];
        repo.Owner.Should().Be("michaeltrefry");
        repo.Name.Should().Be("CodeFlow");
    }

    [Fact]
    public void BuildGoalToolExecutionContext_DropsMalformedRepoEntries()
    {
        // Mirror AgentInvocationConsumer.ResolveRepositoryContexts behaviour — bad entries
        // are skipped silently rather than poisoning the whole context.
        var saga = new WorkflowSagaStateEntity
        {
            TraceId = Guid.NewGuid(),
            TraceWorkDir = $"/app/codeflow/workdir/{Guid.NewGuid():N}",
            RepositoriesJson = """[{ "url": "not-a-url" }, { "url": "https://github.com/a/b.git" }]""",
        };

        var result = GoalNodeDispatcher.BuildGoalToolExecutionContext(saga);

        result!.Repositories.Should().NotBeNull().And.HaveCount(1);
        result.Repositories![0].Name.Should().Be("b");
    }

    [Fact]
    public void Dispatcher_RegistersForGoalNodeKind()
    {
        var dispatcher = new GoalNodeDispatcher();
        dispatcher.Kind.Should().Be(WorkflowNodeKind.Goal);
    }

    private static GoalIterationResult BuildResult(GoalIterationOutcome outcome, string finalOutput)
    {
        return new GoalIterationResult(
            Outcome: outcome,
            Iterations:
            [
                new GoalIterationRecord(1, finalOutput, ToolCallsExecuted: 0, TokenUsage: null,
                    AgentDecision: new AgentDecision("Completed")),
            ],
            FinalHistory: Array.Empty<ChatMessage>(),
            FinalSnapshot: new GoalRuntimeStateSnapshot("obj", null, 0, null, IsCompleteRequested: outcome == GoalIterationOutcome.Success),
            TotalInputTokens: 0,
            TotalOutputTokens: 0,
            TotalToolCallsExecuted: 0);
    }
}
