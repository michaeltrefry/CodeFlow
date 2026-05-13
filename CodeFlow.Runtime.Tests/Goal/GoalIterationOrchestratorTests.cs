using System.Text.Json;
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
    public async Task RunAsync_AcrossIterations_HistoryGrowsLinearly_NoDuplicatedSeed()
    {
        // Regression for the exponential-history-growth bug observed 2026-05-13 in qwen3-35b
        // multi-step testing: input tokens grew from 4k (iter 1) to 100k+ (iter ~10) because the
        // orchestrator was appending the FULL invocationResult.Transcript verbatim, which
        // included the system message + a replay of every previous iteration's transcript. The
        // fix only retains the messages this iteration ADDED — the user message we sent + the
        // assistant/tool exchanges produced in response — and skips the seed (system, prior
        // history).
        //
        // The realistic transcript stub returns: [system, ...history..., user_msg, assistant].
        // After 3 iterations, history should contain 3x [user, assistant] = 6 messages, NOT
        // 3 + 6 + 9 = 18 (the compounding shape).
        var historySizesObserved = new List<int>();
        var invoker = new RecordingAgentInvoker(steps:
        [
            (request, state) =>
            {
                historySizesObserved.Add(request.History?.Count ?? 0);
                return BuildResponse("A", "Completed", 100, 10, 0);
            },
            (request, state) =>
            {
                historySizesObserved.Add(request.History?.Count ?? 0);
                return BuildResponse("B", "Completed", 100, 10, 0);
            },
            (request, state) =>
            {
                historySizesObserved.Add(request.History?.Count ?? 0);
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

        historySizesObserved.Should().HaveCount(3);
        // Each iteration adds exactly one user message + one assistant message (the stub's
        // canned response). So history grows by 2 per iteration: 0, 2, 4.
        historySizesObserved[0].Should().Be(0, "iteration 1 starts fresh with no history");
        historySizesObserved[1].Should().Be(2, "iteration 2 sees iter 1's [user, assistant] — no system");
        historySizesObserved[2].Should().Be(4, "iteration 3 sees iter 1+2's [user, assistant, user, assistant] — linear growth");
    }

    [Fact]
    public async Task RunAsync_AcrossIterations_HistoryContainsOnlyUserAssistantToolMessages_NoSystem()
    {
        // Pin the no-system-in-history invariant. ContextAssembler injects a single system
        // message at position 0 of every InvocationLoop call; including system messages in
        // history would either duplicate them or interleave them mid-conversation. Neither is
        // valid for the provider APIs and both bust the prompt cache.
        var historiesObserved = new List<IReadOnlyList<ChatMessage>?>();
        var invoker = new RecordingAgentInvoker(steps:
        [
            (request, state) =>
            {
                historiesObserved.Add(request.History);
                return BuildResponse("A", "Completed", 0, 0, 0);
            },
            (request, state) =>
            {
                historiesObserved.Add(request.History);
                state.MarkComplete();
                return BuildResponse("B", "Completed", 0, 0, 0);
            },
        ]);
        var orchestrator = new GoalIterationOrchestrator(invoker, Renderer);

        await orchestrator.RunAsync(new GoalIterationRequest(
            Objective: "task",
            TokenBudget: null,
            MaxIterations: 50,
            BaseConfiguration: BaseConfig,
            Tools: ResolvedAgentTools.Empty));

        // Iteration 2's history must be entirely user/assistant/tool — no System.
        historiesObserved[1].Should().NotBeNull();
        historiesObserved[1]!.Should().NotContain(m => m.Role == ChatMessageRole.System,
            "history fed back to the next iteration must never contain System messages — "
            + "ContextAssembler always prepends exactly one system message, and including any "
            + "in history either duplicates it (cache miss) or interleaves it mid-conversation");
    }

    [Fact]
    public async Task RunAsync_EveryIteration_UsesContinuationPromptWithAuditClauses()
    {
        // Iteration-1 audit gap fix (2026-05-13): the continuation prompt is now injected as
        // the user message on EVERY iteration, including the first. Original Codex /goal only
        // injected on iteration 2+, but that left a real audit gap — observed in qwen3 testing
        // where the agent completed work inside one InvocationLoop and never saw the audit
        // checklist before claiming completion. Pin the new behaviour so regression to the old
        // "iteration 1 = bare objective" path is caught at CI.
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
        foreach (var input in observedInputs)
        {
            input.Should().Contain("Completion audit:",
                "every iteration sees the audit checklist so the agent cannot claim completion "
                + "without per-requirement verification, even on a one-iteration run");
            input.Should().Contain("Ship the PR",
                "the continuation template embeds the current objective on every render");
        }
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
    public async Task RunAsync_ModelCallsAbandonImmediately_ExitsAbandonedOnIterationOne()
    {
        var invoker = new RecordingAgentInvoker(steps:
        [
            (request, state) =>
            {
                state.MarkAbandoned("python is unreachable; container.run fails every attempt");
                return BuildResponse("Abandoning.", "Completed", 1000, 100, 1);
            },
        ]);
        var orchestrator = new GoalIterationOrchestrator(invoker, Renderer);

        var result = await orchestrator.RunAsync(new GoalIterationRequest(
            Objective: "Run a Python script",
            TokenBudget: 100_000,
            MaxIterations: 50,
            BaseConfiguration: BaseConfig,
            Tools: ResolvedAgentTools.Empty));

        result.Outcome.Should().Be(GoalIterationOutcome.Abandoned);
        result.Iterations.Should().HaveCount(1);
        invoker.CallCount.Should().Be(1);
        result.FinalSnapshot.IsAbandonRequested.Should().BeTrue();
        result.FinalSnapshot.AbandonReason.Should()
            .Be("python is unreachable; container.run fails every attempt");
        result.FinalSnapshot.IsCompleteRequested.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_AbandonWinsOverBudgetExhaustion()
    {
        // Same priority logic as the Success-over-budget pin: an honest abandon-with-reason is
        // more informative than "ran out of tokens," so it wins when both fire on the same turn.
        var invoker = new RecordingAgentInvoker(steps:
        [
            (request, state) =>
            {
                state.MarkAbandoned("required dependency unreachable");
                return BuildResponse("Abandoning.", "Completed", 9000, 2000, 0);
            },
        ]);
        var orchestrator = new GoalIterationOrchestrator(invoker, Renderer);

        var result = await orchestrator.RunAsync(new GoalIterationRequest(
            Objective: "task",
            TokenBudget: 10_000,
            MaxIterations: 50,
            BaseConfiguration: BaseConfig,
            Tools: ResolvedAgentTools.Empty));

        result.Outcome.Should().Be(GoalIterationOutcome.Abandoned);
        result.FinalSnapshot.TokensUsed.Should().Be(11_000);
        result.FinalSnapshot.AbandonReason.Should().Be("required dependency unreachable");
    }

    [Fact]
    public async Task RunAsync_CompletionWinsOverAbandonOnSameIteration()
    {
        // If a model somehow signalled both on the same turn (it shouldn't — they're mutually
        // exclusive intents — but the tool layer doesn't enforce that), trust the "I made it
        // work" signal over "I gave up." Complete is the stronger claim.
        var invoker = new RecordingAgentInvoker(steps:
        [
            (request, state) =>
            {
                state.MarkAbandoned("changed mind");
                state.MarkComplete();
                return BuildResponse("Done.", "Completed", 500, 100, 1);
            },
        ]);
        var orchestrator = new GoalIterationOrchestrator(invoker, Renderer);

        var result = await orchestrator.RunAsync(new GoalIterationRequest(
            Objective: "task",
            TokenBudget: null,
            MaxIterations: 50,
            BaseConfiguration: BaseConfig,
            Tools: ResolvedAgentTools.Empty));

        result.Outcome.Should().Be(GoalIterationOutcome.Success);
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

    [Fact]
    public async Task RunAsync_Observer_IsThreadedThroughEveryIteration()
    {
        // GN-4: the observer overload of IAgentInvoker.InvokeAsync must receive the same
        // observer instance on every iteration so the per-iteration TokenUsageRecorded
        // events fire individually (rather than being silently dropped by the no-observer
        // overload). Without this thread-through, the trace inspector's per-node token chip
        // shows one final aggregate and hides which iterations were expensive.
        var observer = new RecordingObserver();
        var observersSeen = new List<IInvocationObserver?>();
        var invoker = new ObserverRecordingInvoker(
            observerSink: observersSeen,
            steps:
            [
                BuildResponse("draft", "Completed", 100, 50, 0),
                BuildResponse("revised", "Completed", 100, 50, 0),
                BuildResponse("final", "Completed", 100, 50, 0),
            ],
            completeOn: 3);
        var orchestrator = new GoalIterationOrchestrator(invoker, Renderer);

        await orchestrator.RunAsync(new GoalIterationRequest(
            Objective: "task",
            TokenBudget: null,
            MaxIterations: 50,
            BaseConfiguration: BaseConfig,
            Tools: ResolvedAgentTools.Empty,
            Observer: observer));

        observersSeen.Should().HaveCount(3);
        observersSeen.Should().AllSatisfy(o => o.Should().BeSameAs(observer,
            "the same observer instance must be passed to every iteration so token-usage events accumulate against one stream"));
    }

    [Fact]
    public async Task RunAsync_NullObserver_StillRunsCleanly()
    {
        // Defensive: callers that don't model observability (saga test harnesses without a
        // tokenUsageRecords repository) pass null; the orchestrator must not require it.
        var invoker = new ObserverRecordingInvoker(
            observerSink: new List<IInvocationObserver?>(),
            steps: [BuildResponse("done", "Completed", 0, 0, 0)],
            completeOn: 1);
        var orchestrator = new GoalIterationOrchestrator(invoker, Renderer);

        var result = await orchestrator.RunAsync(new GoalIterationRequest(
            Objective: "task",
            TokenBudget: null,
            MaxIterations: 5,
            BaseConfiguration: BaseConfig,
            Tools: ResolvedAgentTools.Empty,
            Observer: null));

        result.Outcome.Should().Be(GoalIterationOutcome.Success);
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

    /// <summary>
    /// Stub observer used only to capture identity in the threading test. Real observability
    /// is exercised in <c>GoalNodeDispatcherTests</c> with the production observer wiring.
    /// </summary>
    private sealed class RecordingObserver : IInvocationObserver
    {
        public Task OnModelCallStartedAsync(Guid invocationId, int roundNumber, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task OnModelCallCompletedAsync(Guid invocationId, int roundNumber, ChatMessage responseMessage,
            TokenUsage? callTokenUsage, TokenUsage? cumulativeTokenUsage, string provider, string model,
            JsonElement? rawUsage, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task OnToolCallStartedAsync(ToolCall call, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public Task OnToolCallCompletedAsync(ToolCall call, ToolResult result, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Implements the observer overload so the test can record which observer reached the
    /// invoker on each call. Falls back to a per-step canned response.
    /// </summary>
    private sealed class ObserverRecordingInvoker(
        IList<IInvocationObserver?> observerSink,
        IReadOnlyList<AgentInvocationResult> steps,
        int completeOn) : IAgentInvoker
    {
        private int nextStep;

        public Task<AgentInvocationResult> InvokeAsync(
            AgentInvocationConfiguration configuration,
            string? input,
            ResolvedAgentTools tools,
            CancellationToken cancellationToken = default,
            ToolExecutionContext? toolExecutionContext = null)
            => InvokeAsync(configuration, input, tools, observer: null, cancellationToken, toolExecutionContext);

        public Task<AgentInvocationResult> InvokeAsync(
            AgentInvocationConfiguration configuration,
            string? input,
            ResolvedAgentTools tools,
            IInvocationObserver? observer,
            CancellationToken cancellationToken = default,
            ToolExecutionContext? toolExecutionContext = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            observerSink.Add(observer);
            var step = nextStep++;
            // Trigger completion on the configured iteration so the test terminates rather
            // than hitting the iteration cap.
            if (step + 1 == completeOn && configuration.GoalState is MutableGoalRuntimeState state)
            {
                state.MarkComplete();
            }
            return Task.FromResult(steps[step]);
        }
    }

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

            // Simulate the real InvocationLoop's transcript shape: ContextAssembler builds
            // [system, ...history..., user_msg] and the loop appends assistant/tool messages on
            // top of that. The orchestrator's history-dedup logic relies on this prefix being
            // present in invocationResult.Transcript, so the stub must include it (the canned
            // response's transcript is only the NEW assistant/tool exchanges added during the
            // iteration).
            var realisticTranscript = new List<ChatMessage>
            {
                new ChatMessage(ChatMessageRole.System, configuration.SystemPrompt ?? string.Empty),
            };
            if (configuration.History is { Count: > 0 } history)
            {
                realisticTranscript.AddRange(history);
            }
            if (!string.IsNullOrEmpty(input))
            {
                realisticTranscript.Add(new ChatMessage(ChatMessageRole.User, input));
            }
            realisticTranscript.AddRange(response.Transcript);

            return Task.FromResult(response with { Transcript = realisticTranscript });
        }
    }
}
