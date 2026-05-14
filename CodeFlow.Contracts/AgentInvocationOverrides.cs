namespace CodeFlow.Contracts;

/// <summary>
/// Epic 993: a per-workflow-node overlay of a small set of agent invocation properties. Set on
/// an agent-bearing <c>WorkflowNode</c> (Agent / Hitl / Start / Goal) and applied at invocation
/// time on top of the agent's stored configuration — WITHOUT creating a new agent version and
/// WITHOUT forking the agent, so the node stays linked to its source agent. This is the
/// lightweight "nudge" counterpart to in-place agent edit (which forks).
///
/// Every field is nullable and inherit-by-default: a null field means "use the agent's value".
/// </summary>
/// <param name="ModelProvider">Overrides the agent's model provider. Coupled with
/// <paramref name="Model"/> — both set or both null.</param>
/// <param name="Model">Overrides the agent's model. Coupled with <paramref name="ModelProvider"/>.</param>
/// <param name="MaxOutputTokens">Overrides <c>AgentInvocationConfiguration.MaxTokens</c>.</param>
/// <param name="MaxToolCalls">Overrides <c>InvocationLoopBudget.MaxToolCalls</c>.</param>
/// <param name="MaxLoopDurationSeconds">Overrides <c>InvocationLoopBudget.MaxLoopDuration</c>.
/// Transported as whole seconds; mapped to a <c>TimeSpan</c> at the merge point.</param>
/// <param name="MaxConsecutiveNonMutatingCalls">Overrides
/// <c>InvocationLoopBudget.MaxConsecutiveNonMutatingCalls</c>.</param>
/// <param name="AdditionalToolIdentifiers">Additive only — extra tools granted on top of the
/// agent's role-derived tool set. Host tool names and/or <c>mcp:&lt;server&gt;:&lt;tool&gt;</c>
/// identifiers. Never replaces the role's grants.</param>
public sealed record AgentInvocationOverrides(
    string? ModelProvider = null,
    string? Model = null,
    int? MaxOutputTokens = null,
    int? MaxToolCalls = null,
    int? MaxLoopDurationSeconds = null,
    int? MaxConsecutiveNonMutatingCalls = null,
    IReadOnlyList<string>? AdditionalToolIdentifiers = null);
