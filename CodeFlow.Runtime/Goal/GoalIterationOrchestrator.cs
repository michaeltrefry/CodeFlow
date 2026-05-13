using CodeFlow.Runtime.Observability;

namespace CodeFlow.Runtime.Goal;

/// <summary>
/// Epic 978 / GN-3 — the per-goal-node loop. Drives an <see cref="IAgentInvoker"/> across
/// multiple outer iterations against a SINGLE conversation history, injecting the
/// <see cref="GoalContinuationPromptTemplate"/> as a user message before each iteration so
/// the agent's audit prompt steers behavior on every turn. The system prompt + tool
/// schemas are stable across iterations (set once, reused) so the LLM provider's prompt
/// cache hits warm on every iteration after the first.
/// </summary>
/// <remarks>
/// Stop conditions, checked in priority order after each iteration:
/// <list type="number">
///   <item>The agent called <c>goal.update(status="complete")</c> — exit
///     <see cref="GoalIterationOutcome.Success"/>.</item>
///   <item>The token budget is exhausted — exit <see cref="GoalIterationOutcome.BudgetLimited"/>.
///     The model cannot self-pause on budget; the orchestrator owns this exit.</item>
///   <item>The hard iteration cap was reached — exit
///     <see cref="GoalIterationOutcome.IterationCapReached"/> (Failed port at the saga layer).</item>
/// </list>
/// The orchestrator does NOT enforce budget mid-iteration — if the agent overshoots within
/// a single InvocationLoop, that iteration completes and the next-iteration gate catches it.
/// GN-4 may revisit this once we have real-world data on overrun magnitudes.
/// </remarks>
public sealed class GoalIterationOrchestrator
{
    private readonly IAgentInvoker agentInvoker;
    private readonly IScribanTemplateRenderer templateRenderer;

    public GoalIterationOrchestrator(
        IAgentInvoker agentInvoker,
        IScribanTemplateRenderer templateRenderer)
    {
        this.agentInvoker = agentInvoker ?? throw new ArgumentNullException(nameof(agentInvoker));
        this.templateRenderer = templateRenderer ?? throw new ArgumentNullException(nameof(templateRenderer));
    }

    public async Task<GoalIterationResult> RunAsync(
        GoalIterationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Objective);
        if (request.MaxIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.MaxIterations,
                "MaxIterations must be > 0.");
        }

        var state = new MutableGoalRuntimeState(request.Objective, request.TokenBudget);
        var history = new List<ChatMessage>(request.SeedHistory ?? Array.Empty<ChatMessage>());
        var iterations = new List<GoalIterationRecord>();
        int totalInputTokens = 0;
        int totalOutputTokens = 0;
        int totalToolCalls = 0;

        for (var iteration = 1; iteration <= request.MaxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Clear the per-iteration completion signal before invoking. A `goal.update(complete)`
            // call in iteration N-1 should not falsely exit iteration N before the agent has had
            // a chance to act. (The completion check below DOES inspect the post-invocation
            // snapshot, so this only matters if MarkComplete somehow leaks across invocations —
            // defence-in-depth.)
            state.ClearCompleteRequested();

            // The continuation prompt — with its anti-laziness audit checklist — is injected
            // as the user message on EVERY iteration, including the first. The original Codex
            // /goal design only injected on iteration 2+, but that left a real audit gap: if
            // the agent's tool budget was enough to complete the work in one InvocationLoop,
            // the audit clauses never reached it, and a less-aligned model could claim
            // completion without the per-requirement verification check (observed in qwen3
            // testing 2026-05-13). Always-rendering closes the gap: the agent sees the full
            // objective + audit checklist + budget block from turn 1, the cached system-prompt
            // prefix is unaffected (the template is a user message, not a system mutation),
            // and the wording is general enough to read fine on turn 1 ("make concrete progress
            // toward the real requested end state" applies just as much to a fresh start).
            var userMessage = GoalContinuationPromptTemplate.Render(templateRenderer, state.Snapshot());

            // Configuration is rebuilt per iteration so History and GoalState reflect the current
            // run, but everything else (system prompt, tool schemas, partials, decision templates)
            // is byte-identical across iterations — the property the LLM provider's prompt cache
            // relies on for warm hits.
            var iterationConfig = request.BaseConfiguration with
            {
                History = history.Count == 0 ? null : history.ToArray(),
                GoalState = state,
            };

            // Thread the observer through every iteration so the token-usage capture observer
            // (epic 978 / GN-4) fires `TokenUsageRecorded` events PER iteration — not just
            // rolled up at the end. Without this, the trace inspector's per-node token chip
            // would show one aggregate, hiding which iterations were expensive.
            // IInvocationObserver.InvokeAsync overload falls back to the no-observer overload
            // when the observer is null, so test callers that don't care can pass null.
            var invocationResult = await agentInvoker.InvokeAsync(
                iterationConfig,
                input: userMessage,
                request.Tools,
                request.Observer,
                cancellationToken,
                request.ToolExecutionContext);

            // Append this iteration's transcript so the next iteration sees full history.
            // The invocation's transcript starts with the user message we passed and ends with
            // the final assistant message + any intervening tool exchanges.
            history.AddRange(invocationResult.Transcript);

            // Token accounting. The state's tokens_used drives the next iteration's continuation
            // prompt + `goal.get` answers. Input + output are summed because the cap is
            // cumulative (matches Codex's accounting).
            if (invocationResult.TokenUsage is { } usage)
            {
                state.AddTokensUsed(usage.InputTokens + usage.OutputTokens);
                totalInputTokens = checked(totalInputTokens + usage.InputTokens);
                totalOutputTokens = checked(totalOutputTokens + usage.OutputTokens);
            }
            totalToolCalls = checked(totalToolCalls + invocationResult.ToolCallsExecuted);

            iterations.Add(new GoalIterationRecord(
                IterationNumber: iteration,
                Output: invocationResult.Output,
                ToolCallsExecuted: invocationResult.ToolCallsExecuted,
                TokenUsage: invocationResult.TokenUsage,
                AgentDecision: invocationResult.Decision));

            // Exit checks, in priority order. The agent's completion claim wins over a
            // simultaneous budget exhaustion — if both fire on the same iteration we honor the
            // model's signal that the work is verified done.
            if (state.Snapshot().IsCompleteRequested)
            {
                return BuildResult(
                    GoalIterationOutcome.Success,
                    iterations,
                    history,
                    state,
                    totalInputTokens,
                    totalOutputTokens,
                    totalToolCalls);
            }

            if (state.IsBudgetExhausted())
            {
                return BuildResult(
                    GoalIterationOutcome.BudgetLimited,
                    iterations,
                    history,
                    state,
                    totalInputTokens,
                    totalOutputTokens,
                    totalToolCalls);
            }
        }

        return BuildResult(
            GoalIterationOutcome.IterationCapReached,
            iterations,
            history,
            state,
            totalInputTokens,
            totalOutputTokens,
            totalToolCalls);
    }

    private static GoalIterationResult BuildResult(
        GoalIterationOutcome outcome,
        IReadOnlyList<GoalIterationRecord> iterations,
        IReadOnlyList<ChatMessage> history,
        MutableGoalRuntimeState state,
        int totalInputTokens,
        int totalOutputTokens,
        int totalToolCalls)
    {
        var snapshot = state.Snapshot();
        return new GoalIterationResult(
            Outcome: outcome,
            Iterations: iterations,
            FinalHistory: history,
            FinalSnapshot: snapshot,
            TotalInputTokens: totalInputTokens,
            TotalOutputTokens: totalOutputTokens,
            TotalToolCallsExecuted: totalToolCalls);
    }
}

/// <summary>The arguments handed to <see cref="GoalIterationOrchestrator.RunAsync"/>.</summary>
/// <param name="Objective">The rendered objective text (Scriban-rendered against workflow vars
/// by the saga before reaching this layer). Becomes the first user message; also visible to the
/// agent via <c>goal.get</c>.</param>
/// <param name="TokenBudget">Cumulative token cap across all iterations; null for unbounded.</param>
/// <param name="MaxIterations">Hard runaway-protection cap. Default 50 per validator constants;
/// authors can override on the Goal node.</param>
/// <param name="BaseConfiguration">The base <see cref="AgentInvocationConfiguration"/> with system
/// prompt, prompt template, partials, etc. already populated. The orchestrator overrides
/// <c>History</c> and <c>GoalState</c> per iteration; everything else is preserved.</param>
/// <param name="Tools">Resolved agent tools (role grants). The orchestrator does not modify these
/// between iterations.</param>
/// <param name="ToolExecutionContext">Per-trace workspace context; preserved across iterations so
/// the agent's file edits accumulate across turns.</param>
/// <param name="SeedHistory">Optional history to seed the conversation with (e.g. a system-set
/// preamble from the saga). The orchestrator appends iteration transcripts to this.</param>
/// <param name="Observer">Optional per-iteration invocation observer (epic 978 / GN-4). When set,
/// the orchestrator passes it through to every <c>IAgentInvoker.InvokeAsync</c> call so each
/// iteration's <c>TokenUsageRecorded</c> events fire individually — without this, the trace
/// inspector's per-node token chip shows only the final aggregate and hides which iterations
/// were expensive. Null is fine for tests that don't model the observability path.</param>
public sealed record GoalIterationRequest(
    string Objective,
    int? TokenBudget,
    int MaxIterations,
    AgentInvocationConfiguration BaseConfiguration,
    ResolvedAgentTools Tools,
    ToolExecutionContext? ToolExecutionContext = null,
    IReadOnlyList<ChatMessage>? SeedHistory = null,
    IInvocationObserver? Observer = null);

/// <summary>Outcome categories the saga routes on. <c>Failed</c> is implicit at the saga layer.</summary>
public enum GoalIterationOutcome
{
    /// <summary>The agent called <c>goal.update(status="complete")</c> and the audit prompt's
    /// requirements were satisfied. Saga routes via the <c>Success</c> port.</summary>
    Success,

    /// <summary>The token budget was exhausted before the agent claimed completion. Saga
    /// routes via the <c>BudgetLimited</c> port — a HITL gate can extend or close out.</summary>
    BudgetLimited,

    /// <summary>The runaway-protection iteration cap was hit. Saga routes via the implicit
    /// <c>Failed</c> port with a clear reason in the decision payload.</summary>
    IterationCapReached,
}

/// <summary>Per-iteration record. Surfaces to trace evidence bundles + token usage UI.</summary>
public sealed record GoalIterationRecord(
    int IterationNumber,
    string Output,
    int ToolCallsExecuted,
    TokenUsage? TokenUsage,
    AgentDecision AgentDecision);

/// <summary>The full result of a Goal run. Carries enough state for the saga to publish a
/// synthetic <c>AgentInvocationCompleted</c> on the appropriate port.</summary>
public sealed record GoalIterationResult(
    GoalIterationOutcome Outcome,
    IReadOnlyList<GoalIterationRecord> Iterations,
    IReadOnlyList<ChatMessage> FinalHistory,
    GoalRuntimeStateSnapshot FinalSnapshot,
    int TotalInputTokens,
    int TotalOutputTokens,
    int TotalToolCallsExecuted);
