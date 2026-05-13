using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.Goal;

/// <summary>
/// Epic 978 / GN-2 — the tool provider that surfaces <c>goal.get</c> and <c>goal.update</c> to
/// an agent running inside a <see cref="Persistence.WorkflowNodeKind.Goal"/> node. Ported in
/// spirit from Codex <c>goal_spec.rs</c> with one intentional simplification: there is no
/// <c>create_goal</c> tool (the workflow owns goal creation). The <c>goal.update</c> status enum
/// is <c>["complete", "abandon"]</c> — two honest exits: complete when the objective is verified
/// done, abandon when the environment makes it impossible. The abandon path is the GN-7 addition
/// after observing qwen3 cheat the audit (Perl-as-python3 in trace 14) when the legitimate path
/// kept failing; without an abandon-exit the model has no honest way to fail.
/// </summary>
/// <remarks>
/// Scope: the provider is only constructed and yielded into the tool registry when the agent
/// invocation's <see cref="AgentInvocationConfiguration.GoalState"/> is non-null. That property
/// is exclusively set by the Goal-node executor (GN-3); Agent / Hitl / Subflow / ReviewLoop /
/// Swarm / Transform / ForEach invocations and the homepage assistant never construct this
/// provider, so the tools cannot leak into their surfaces.
/// </remarks>
public sealed class GoalHostToolProvider : IToolProvider
{
    public const string GetGoalToolName = "goal.get";
    public const string UpdateGoalToolName = "goal.update";
    public const string CompleteStatusValue = "complete";
    public const string AbandonStatusValue = "abandon";

    private readonly IGoalRuntimeState state;

    public GoalHostToolProvider(IGoalRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        this.state = state;
    }

    public ToolCategory Category => ToolCategory.Goal;

    public IReadOnlyList<ToolSchema> AvailableTools(ToolAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var limit = policy.GetCategoryLimit(Category);
        if (limit <= 0)
        {
            return [];
        }

        return GetCatalog().Take(limit).ToArray();
    }

    public Task<ToolResult> InvokeAsync(
        ToolCall toolCall,
        CancellationToken cancellationToken = default,
        ToolExecutionContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        return toolCall.Name switch
        {
            GetGoalToolName => Task.FromResult(HandleGet(toolCall)),
            UpdateGoalToolName => Task.FromResult(HandleUpdate(toolCall)),
            _ => throw new UnknownToolException(toolCall.Name),
        };
    }

    private ToolResult HandleGet(ToolCall toolCall)
    {
        var snapshot = state.Snapshot();
        var payload = new JsonObject
        {
            ["objective"] = snapshot.Objective,
            ["tokenBudget"] = snapshot.TokenBudget,
            ["tokensUsed"] = snapshot.TokensUsed,
            ["tokensRemaining"] = snapshot.TokensRemaining,
        };
        return new ToolResult(toolCall.Id, payload.ToJsonString());
    }

    private ToolResult HandleUpdate(ToolCall toolCall)
    {
        // Two accepted statuses:
        //   - "complete" — the audit passed; exit Success.
        //   - "abandon"  — the objective is environmentally impossible; exit Abandoned with a
        //     reason a postmortem / HITL gate can act on.
        // Other statuses ("paused", "blocked", "budget-limited", etc.) remain typed errors —
        // those transitions are owned by the executor / user, not the model.
        if (toolCall.Arguments is not JsonObject args)
        {
            return Error(toolCall, "goal.update requires an object payload with a `status` field.");
        }

        if (!args.TryGetPropertyValue("status", out var statusNode) || statusNode is null)
        {
            return Error(toolCall,
                "goal.update requires the `status` field. Accepted values: \"complete\" or \"abandon\".");
        }

        if (statusNode is not JsonValue value || !value.TryGetValue<string>(out var status))
        {
            return Error(toolCall,
                "goal.update `status` must be a string. Accepted values: \"complete\" or \"abandon\".");
        }

        return status switch
        {
            CompleteStatusValue => HandleComplete(toolCall),
            AbandonStatusValue => HandleAbandon(toolCall, args),
            _ => Error(
                toolCall,
                $"goal.update `status` = \"{status}\" is not accepted. Accepted values: \"complete\" "
                + "(when the audit has verified every requirement) or \"abandon\" (when the "
                + "objective is environmentally impossible — include a `reason` field explaining "
                + "what blocked you). Do not invent pause / blocked / budget-limited statuses; "
                + "those transitions are owned by the Goal-node executor and the user, not the model."),
        };
    }

    private ToolResult HandleComplete(ToolCall toolCall)
    {
        state.MarkComplete();
        return new ToolResult(toolCall.Id, """{"acknowledged":true,"status":"complete"}""");
    }

    private ToolResult HandleAbandon(ToolCall toolCall, JsonObject args)
    {
        // Abandon REQUIRES a non-empty reason so the downstream port (postmortem / HITL) has
        // something to act on. A reason-less abandon is almost always the model giving up after
        // one failed attempt; requiring the field forces it to articulate the specific blocker,
        // which tends to surface "the environment is broken" vs "I can't be bothered."
        if (!args.TryGetPropertyValue("reason", out var reasonNode) || reasonNode is null)
        {
            return Error(toolCall,
                "goal.update(abandon) requires a `reason` field — a concrete description of what "
                + "blocked progress (e.g. \"container.run consistently rejects every legitimate "
                + "approach with workspace_invalid; Python is unreachable in this environment\"). "
                + "Without a reason the downstream handler has no signal to act on.");
        }

        if (reasonNode is not JsonValue reasonValue || !reasonValue.TryGetValue<string>(out var reason))
        {
            return Error(toolCall, "goal.update(abandon) `reason` must be a string.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error(toolCall,
                "goal.update(abandon) `reason` must be a non-empty string explaining the specific blocker.");
        }

        state.MarkAbandoned(reason);

        var ack = new JsonObject
        {
            ["acknowledged"] = true,
            ["status"] = AbandonStatusValue,
            ["reason"] = reason,
        };
        return new ToolResult(toolCall.Id, ack.ToJsonString());
    }

    private static ToolResult Error(ToolCall toolCall, string message) =>
        new(toolCall.Id, message, IsError: true);

    /// <summary>
    /// Tool catalogue. Hand-rolled JsonNode schemas (matches the existing
    /// <see cref="HostToolProvider.GetCatalog"/> pattern). Both Anthropic and OpenAI Responses-API
    /// strict validators are satisfied: every property has a `type`, the `status` enum lists a
    /// single value, and `goal.get` declares an empty properties object (not absent) so the
    /// schema is unambiguous (see feedback memory <c>tool_array_needs_items</c>).
    /// </summary>
    public static IReadOnlyList<ToolSchema> GetCatalog()
    {
        return
        [
            new ToolSchema(
                GetGoalToolName,
                "Get the current goal for this run, including objective, token budget, tokens used, "
                + "and tokens remaining. The audit prompt directs you to read this when reasoning "
                + "about whether enough budget remains for additional verification — never to read "
                + "it as justification for stopping early.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject(),
                    ["additionalProperties"] = false,
                }),
            new ToolSchema(
                UpdateGoalToolName,
                "Exit the goal-run loop. Two honest exits:\n"
                + "  • status=\"complete\" — call this ONLY after the completion audit has "
                + "passed every requirement against authoritative current state. Do not mark "
                + "complete because the budget is nearly exhausted, because you are stopping work, "
                + "or because a workaround satisfies the requirement *literally* but not "
                + "*spiritually*. The audit is your honesty contract.\n"
                + "  • status=\"abandon\" with a `reason` — call this when the objective is "
                + "ENVIRONMENTALLY IMPOSSIBLE. Examples: a required tool consistently rejects "
                + "every legitimate approach with the same error; an external dependency the "
                + "objective assumes is unreachable; a prerequisite the workflow promised does "
                + "not exist. The `reason` must be a concrete, specific description of the "
                + "blocker — not a vague \"I tried and it failed.\" Downstream handling routes "
                + "this to a postmortem agent or HITL gate that investigates whether the "
                + "objective was misposed or the environment is genuinely broken. Do NOT "
                + "abandon because the work is hard, or because the budget is tight, or "
                + "because you would prefer not to do it; abandon is for impossibility, not "
                + "for inconvenience. Other statuses (pause / blocked / budget-limited) are "
                + "owned by the Goal-node executor and the user, not the model.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["status"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray(CompleteStatusValue, AbandonStatusValue),
                            ["description"] = "Either \"complete\" (audit passed) or \"abandon\" "
                                + "(environmentally impossible — include `reason`).",
                        },
                        ["reason"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Required when status=\"abandon\". A concrete, "
                                + "specific description of what blocked progress. Ignored when "
                                + "status=\"complete\".",
                        },
                    },
                    ["required"] = new JsonArray("status"),
                    ["additionalProperties"] = false,
                },
                IsMutating: true),
        ];
    }
}
