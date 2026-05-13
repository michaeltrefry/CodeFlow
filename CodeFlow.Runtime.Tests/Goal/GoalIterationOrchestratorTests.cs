using System.Text.Json.Nodes;
using CodeFlow.Runtime.Goal;
using FluentAssertions;

namespace CodeFlow.Runtime.Tests.Goal;

/// <summary>
/// Epic 978 / GN-3 — end-to-end coverage for the loop semantics. Uses a recording
/// <see cref="IAgentInvoker"/> stub so tests pin the exact contract the orchestrator depends
/// on: per-iteration <c>History</c> grows, <c>GoalState</c> is the same instance across
/// iterations (cache-stability property), tokens accumulate, and the three exit ports fire
/// for the right reasons.
/// </summary>
public sealed class GoalIterationOrchestratorTests
{
    private static readonly IScribanTemplateRenderer Renderer = new ScribanTemplateRenderer(
        renderTimeout: TimeSpan.FromSeconds(2));

    private static readonly AgentInvocationConfiguration BaseConfig = new(
        Provider: "test",
        Model: "test-model",
        SystemPrompt: "You are a goal-runner.");

    [Fact]
    public async Task RunAsync_ModelCallsCompleteImmediately_ExitsSuccessOnIterationOne()
    {
        var invoker = new RecordingAgentInvoker(steps:
        [
            (request, state) =>
            {
                state.MarkComplete();
                return BuildResponse(
                    output: "Done.",
                    portName: "Completed",
                    inputTokens: 1500,
                    outputTokens: 200,
                    toolCalls: 1);
            },
        ]);
        var orchestrator = new GoalIterationOrchestrator(invoker, Renderer);

        var result = await orchestrator.RunAsync(new GoalIterationRequest(
            Objective: "Complete this story",
            TokenBudget: 100_000,
            MaxIterations: 50,
            BaseConfiguration: BaseConfig,
            Tools: ResolvedAgentTools.Empty));

        result.Outcome.Should().Be(GoalIterationOutcome.Success);
        result.Iterations.Should().HaveCount(1);
        invoker.CallCount.Should().Be(1);
        result.FinalSnapshot.IsCompleteRequested.Should().BeTrue();
        result.TotalInputTokens.Should().Be(1500);
        result.TotalOutputTokens.Should().Be(200);
        result.TotalToolCallsExecuted.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_ModelDeliberates_ThenCompletesOnIterationThree()
    {
        var invoker = new RecordingAgentInvoker(steps:
        [
            (request, state) => BuildResponse("Investigating.", "Completed", 1000, 100, 2),
            (request, state) => BuildResponse("Implementing.", "Completed", 2000, 200, 5),
            (request, state) =>
            {
                state.MarkComplete();
                return BuildResponse("Verified done.", "Completed", 500, 50, 1);
            },
        ]);
        var orchestrator = new GoalIterationOrchestrator(invoker, Renderer);

        var result = await orchestrator.RunAsync(new GoalIterationRequest(
            Objective: "Multi-step task",
            TokenBudget: 50_000,
            MaxIterations: 50,
            BaseConfiguration: BaseConfig,
            Tools: ResolvedAgentTools.Empty));

        result.Outcome.Should().Be(GoalIterationOutcome.Success);
        result.Iterations.Should().HaveCount(3);
        result.TotalInputTokens.Should().Be(3500);
        result.TotalOutputTokens.Should().Be(350);
        result.TotalToolCallsExecuted.Should().Be(8);
        result.FinalSnapshot.TokensUsed.Should().Be(3850);
        result.FinalSnapshot.TokensRemaining.Should().Be(46_150);
    }

    [Fact]
    public async Task RunAsync_AcrossIterations_PassesGrowingHistoryAndSameGoalState()
    {
        // Cache-stability + state-continuity invariants: every iteration must see (a) the
        // accumulated transcript so the conversation continues, and (b) the SAME
        // IGoalRuntimeState instance so a previous turn's MarkComplete is visible to a later
        // turn's `goal.get`.
        IGoalRuntimeState? firstSeenState = null;
        var historiesObserved = new List<int>();
        var invoker = new RecordingAgentInvoker(steps:
        [
            (request, state) =>
            {
                firstSeenState = state;
                historiesObserved.Add(request.History?.Count ?? 0);
                return BuildResponse("A", "Completed", 100, 10, 0);
            },
            (request, state) =>
            {
                state.Should().BeSameAs(firstSeenState,
                    "GoalState identity must be stable across iterations so MarkComplete propagates");
                historiesObserved.Add(request.History?.Count ?? 0);
                return BuildResponse("B", "Completed", 100, 10, 0);
            },
            (request, state) =>
            {
                state.Should().BeSameAs(firstSeenState);
                historiesObserved.Add(request.History?.Count ?? 0);
                state.MarkComplete();
                return BuildResponse("C", "Completed", 100, 10, 0);
            },
        ]);
        var orchestrator = new GoalIterationOrchestrator(invoker, Renderer);

        await orchestrator.RunAsync(new GoalIterationRequest(
            Objective: "task",
            TokenBudget: null,
            MaxIterations: 50,
            BaseConfiguration: BaseConfig,
            Tools: ResolvedAgentTools.Empty));

        historiesObserved.Should().HaveCount(3);
        historiesObserved[0].Should().Be(0, "iteration 1 starts fresh");
        historiesObserved[1].Should().BeGreaterThan(historiesObserved[0],
            "iteration 2 sees iteration 1's transcript");
        historiesObserved[2].Should().BeGreaterThan(historiesObserved[1],
            "iteration 3 sees iterations 1 + 2's transcripts");
    }

    [Fact]
    public async Task RunAsync_SystemPromptIdentical_AcrossIterations_ForCacheStability()
    {
        // The continuation prompt arrives as the per-iteration USER message, not as a change to
        // the system prompt. This test pins that invariant — if a future change starts mutating
        // SystemPrompt across iterations, prompt caching will miss on every turn and the cost
        // argument for Goal-vs-multi-agent-handoff collapses.
        var observedSystemPrompts = new List<string?>();
        var invoker = new RecordingAgentInvoker(steps:
        [
            (request, state) =>
            {
                observedSystemPrompts.Add(request.Configuration.SystemPrompt);
                return BuildResponse("A", "Completed", 0, 0, 0);
            },
            (request, state) =>
            {
                observedSystemPrompts.Add(request.Configuration.SystemPrompt);
                return BuildResponse("B", "Completed", 0, 0, 0);
            },
            (request, state) =>
            {
                observedSystemPrompts.Add(request.Configuration.SystemPrompt);
                state.MarkComplete();
                return BuildResponse("C", "Completed", 0, 0, 0);
            },
        ]);
        var orchestrator = new GoalIterationOrchestrator(invoker, Renderer);

        await orchestrator.RunAsync(new GoalIterationRequest(
            Objective: "task",
            TokenBudget: null,
            MaxIterations: 50,
            BaseConfiguration: BaseConfig,
            Tools: ResolvedAgentTools.Empty));

        observedSystemPrompts.Should().AllBe(BaseConfig.SystemPrompt,
            "system prompt is byte-identical across iterations so the cache prefix stays warm");
    }

    [Fact]
    public async Task RunAsync_FirstIteration_UsesBareObjective_AsUserMessage()
    {
        // GN-3 contract: iteration 1's user message is the rendered objective only, NOT the
        // continuation prompt (which assumes prior turns). The audit prompt arrives on
        // iteration 2+ when there's actually a prior turn to audit against.
        var observedInputs = new List<string?>();
        var invoker = new RecordingAgentInvoker(steps:
        [
            (request, state) =>
            {
                observedInputs.Add(request.Input);
                return BuildResponse("A", "Completed", 0, 0, 0);
            },
            (request, state) =>
            {
                observedInputs.Add(request.Input);
                state.MarkComplete();
                return BuildResponse("B", "Completed", 0, 0, 0);
            },
        ]);
        var orchestrator = new GoalIterationOrchestrator(invoker, Renderer);

        await orchestrator.RunAsync(new GoalIterationRequest(
            Objective: "Ship the PR",
            TokenBudget: null,
            MaxIterations: 50,
            BaseConfiguration: BaseConfig,
            Tools: ResolvedAgentTools.Empty));

        observedInputs.Should().HaveCount(2);
        observedInputs[0].Should().Be("Ship the PR", "iteration 1 user message is the bare objective");
        observedInputs[1].Should().Contain("Completion audit:",
            "iteration 2+ user message is the continuation prompt with the audit clauses");
        observedInputs[1].Should().Contain("Ship the PR",
            "continuation prompt embeds the current objective");
    }

    [Fact]
    public async Task RunAsync_BudgetExhausted_ExitsBudgetLimited_WithoutInfiniteLoop()
    {
        // The system owns budget enforcement (Codex contract). The model cannot stop itself on
        // budget pressure, but the orchestrator MUST cap consumption. Each iteration here
        // consumes 6,000 tokens against a 10,000-budget — second iteration crosses the cap and
        // BudgetLimited fires before iteration 3 starts.
        var invoker = new RecordingAgentInvoker(steps:
        [
            (request, state) => BuildResponse("Working.", "Completed", 5000, 1000, 0),
            (request, state) => BuildResponse("Still working.", "Completed", 5000, 1000, 0),
            (request, state) => BuildResponse("Should never run.", "Completed", 0, 0, 0),
        ]);
        var orchestrator = new GoalIterationOrchestrator(invoker, Renderer);

        var result = await orchestrator.RunAsync(new GoalIterationRequest(
            Objective: "task",
            TokenBudget: 10_000,
            MaxIterations: 50,
            BaseConfiguration: BaseConfig,
            Tools: ResolvedAgentTools.Empty));

        result.Outcome.Should().Be(GoalIterationOutcome.BudgetLimited);
        invoker.CallCount.Should().Be(2, "third iteration must not start once the budget is exhausted");
        result.FinalSnapshot.TokensUsed.Should().Be(12_000);
        result.FinalSnapshot.TokensRemaining.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_IterationCapReached_ExitsWithIterationCapReachedOutcome()
    {
        var invoker = new RecordingAgentInvoker(steps: Enumerable.Range(0, 10).Select(_ =>
            (Func<RecordedInvocationRequest, MutableGoalRuntimeState, AgentInvocationResult>)(
                (request, state) => BuildResponse("looping", "Completed", 10, 5, 0))).ToArray());
        var orchestrator = new GoalIterationOrchestrator(invoker, Renderer);

        var result = await orchestrator.RunAsync(new GoalIterationRequest(
            Objective: "task",
            TokenBudget: null,        // unbounded — only the iteration cap can stop it
            MaxIterations: 5,         // small cap for the test
            BaseConfiguration: BaseConfig,
            Tools: ResolvedAgentTools.Empty));

        result.Outcome.Should().Be(GoalIterationOutcome.IterationCapReached);
        result.Iterations.Should().HaveCount(5);
        invoker.CallCount.Should().Be(5);
    }

    [Fact]
    public async Task RunAsync_CompletionWinsOverSimultaneousBudgetExhaustion()
    {
        // Priority pin: when MarkComplete + budget exhaustion fire on the same iteration, the
        // model's verified-done signal wins. The orchestrator routes to Success, not
        // BudgetLimited.
        var invoker = new RecordingAgentInvoker(steps:
        [
            (request, state) =>
            {
                state.MarkComplete();
                return BuildResponse("Done.", "Completed", 9000, 2000, 0);
            },
        ]);
        var orchestrator = new GoalIterationOrchestrator(invoker, Renderer);

        var result = await orchestrator.RunAsync(new GoalIterationRequest(
            Objective: "task",
            TokenBudget: 10_000,
            MaxIterations: 50,
            BaseConfiguration: BaseConfig,
            Tools: ResolvedAgentTools.Empty));

        result.Outcome.Should().Be(GoalIterationOutcome.Success);
        result.FinalSnapshot.TokensUsed.Should().Be(11_000);
    }

    [Fact]
    public async Task RunAsync_CancellationToken_StopsLoopBetweenIterations()
    {
        using var cts = new CancellationTokenSource();
        var invoker = new RecordingAgentInvoker(steps:
        [
            (request, state) =>
            {
                cts.Cancel();
                return BuildResponse("iteration ran", "Completed", 100, 50, 0);
            },
            (request, state) => BuildResponse("should not run", "Completed", 0, 0, 0),
        ]);
        var orchestrator = new GoalIterationOrchestrator(invoker, Renderer);

        await Assert.ThrowsAsync<OperationCanceledException>(() => orchestrator.RunAsync(
            new GoalIterationRequest(
                Objective: "task",
                TokenBudget: null,
                MaxIterations: 50,
                BaseConfiguration: BaseConfig,
                Tools: ResolvedAgentTools.Empty),
            cts.Token));

        invoker.CallCount.Should().Be(1, "cancellation is observed at the iteration boundary");
    }

    [Fact]
    public async Task RunAsync_BlankObjective_Throws()
    {
        var orchestrator = new GoalIterationOrchestrator(new RecordingAgentInvoker([]), Renderer);

        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.RunAsync(new GoalIterationRequest(
            Objective: "  ",
            TokenBudget: null,
            MaxIterations: 50,
            BaseConfiguration: BaseConfig,
            Tools: ResolvedAgentTools.Empty)));
    }

    [Fact]
    public async Task RunAsync_NonPositiveMaxIterations_Throws()
    {
        var orchestrator = new GoalIterationOrchestrator(new RecordingAgentInvoker([]), Renderer);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => orchestrator.RunAsync(new GoalIterationRequest(
            Objective: "task",
            TokenBudget: null,
            MaxIterations: 0,
            BaseConfiguration: BaseConfig,
            Tools: ResolvedAgentTools.Empty)));
    }

    private static AgentInvocationResult BuildResponse(
        string output,
        string portName,
        int inputTokens,
        int outputTokens,
        int toolCalls)
    {
        return new AgentInvocationResult(
            Output: output,
            Decision: new AgentDecision(portName, Payload: null),
            Transcript: [new ChatMessage(ChatMessageRole.Assistant, output)],
            TokenUsage: new TokenUsage(inputTokens, outputTokens, inputTokens + outputTokens),
            ToolCallsExecuted: toolCalls);
    }

    private sealed record RecordedInvocationRequest(
        AgentInvocationConfiguration Configuration,
        string? Input,
        IReadOnlyList<ChatMessage>? History);

    private sealed class RecordingAgentInvoker : IAgentInvoker
    {
        private readonly IReadOnlyList<Func<RecordedInvocationRequest, MutableGoalRuntimeState, AgentInvocationResult>> steps;
        private int nextStep;

        public int CallCount { get; private set; }

        public RecordingAgentInvoker(
            IReadOnlyList<Func<RecordedInvocationRequest, MutableGoalRuntimeState, AgentInvocationResult>> steps)
        {
            this.steps = steps;
        }

        public Task<AgentInvocationResult> InvokeAsync(
            AgentInvocationConfiguration configuration,
            string? input,
            ResolvedAgentTools tools,
            CancellationToken cancellationToken = default,
            ToolExecutionContext? toolExecutionContext = null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (nextStep >= steps.Count)
            {
                throw new InvalidOperationException(
                    $"RecordingAgentInvoker exhausted at call #{CallCount + 1} but the orchestrator kept iterating.");
            }

            CallCount++;
            var record = new RecordedInvocationRequest(configuration, input, configuration.History);
            var state = configuration.GoalState as MutableGoalRuntimeState
                ?? throw new InvalidOperationException(
                    "GoalIterationOrchestrator must pass a MutableGoalRuntimeState; got " + configuration.GoalState?.GetType().Name);
            var response = steps[nextStep++](record, state);
            return Task.FromResult(response);
        }
    }
}
