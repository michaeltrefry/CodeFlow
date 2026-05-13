using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Runtime.Goal;

/// <summary>
/// Epic 978 / GN-2 — the tool provider that surfaces <c>goal.get</c> and <c>goal.update</c> to
/// an agent running inside a <see cref="Persistence.WorkflowNodeKind.Goal"/> node. Ported in
/// spirit from Codex <c>goal_spec.rs</c> with two intentional simplifications: there is no
/// <c>create_goal</c> tool (the workflow owns goal creation), and <c>goal.update</c>'s status
/// enum is <c>["complete"]</c> only — the model has exactly one exit.
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
        // Status enum is `["complete"]` only — verbatim port of Codex goal_spec.rs:62-92. The
        // model has exactly one exit; "paused", "abandoned", "blocked", "failed" are all
        // typed errors so the model can't open an escape hatch the audit-prompt isn't designed
        // to handle.
        if (toolCall.Arguments is not JsonObject args)
        {
            return Error(toolCall, "goal.update requires an object payload with a `status` field.");
        }

        if (!args.TryGetPropertyValue("status", out var statusNode) || statusNode is null)
        {
            return Error(toolCall, "goal.update requires the `status` field. The only accepted value is \"complete\".");
        }

        if (statusNode is not JsonValue value || !value.TryGetValue<string>(out var status))
        {
            return Error(toolCall, "goal.update `status` must be a string equal to \"complete\".");
        }

        if (!string.Equals(status, CompleteStatusValue, StringComparison.Ordinal))
        {
            return Error(
                toolCall,
                $"goal.update `status` = \"{status}\" is not accepted. The only accepted value is \"complete\". "
                + "Do not invent pause/abandon/budget-limited statuses — those transitions are owned by the "
                + "Goal-node executor and the user, not the model.");
        }

        state.MarkComplete();
        return new ToolResult(toolCall.Id, """{"acknowledged":true,"status":"complete"}""");
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
                "Mark the active goal complete. Call this ONLY after the completion audit has "
                + "passed every requirement against authoritative current state. Do not mark "
                + "complete because the budget is nearly exhausted or because you are stopping "
                + "work. The only accepted status value is \"complete\"; pause / resume / "
                + "budget-limited / abandoned are owned by the Goal-node executor and the user.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["status"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray(CompleteStatusValue),
                            ["description"] = "The only accepted value is \"complete\".",
                        },
                    },
                    ["required"] = new JsonArray("status"),
                    ["additionalProperties"] = false,
                },
                IsMutating: true),
        ];
    }
}
