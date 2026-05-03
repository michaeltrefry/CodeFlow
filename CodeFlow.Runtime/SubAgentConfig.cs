namespace CodeFlow.Runtime;

/// <summary>
/// Enables a parent agent to spawn anonymous sub-agent workers via the runtime-provided
/// <c>spawn_subagent</c> tool. Sub-agents are not pre-configured slots: the parent describes
/// each task and the response shape it wants at spawn time. Sub-agents inherit the parent's
/// resolved tool set (sc-571 v1; no per-spawn role assignment).
/// </summary>
/// <param name="Provider">Optional provider override; null inherits the parent's provider.</param>
/// <param name="Model">Optional model override; null inherits the parent's model.</param>
/// <param name="MaxConcurrent">
/// Safety cap on the number of sub-agents that can run in parallel from a single
/// <c>spawn_subagent</c> tool call. Defaults to 4.
/// </param>
/// <param name="MaxTokens">Optional per-spawn max-tokens override; null inherits the parent's.</param>
/// <param name="Temperature">Optional per-spawn temperature override; null inherits the parent's.</param>
public sealed record SubAgentConfig(
    string? Provider = null,
    string? Model = null,
    int MaxConcurrent = 4,
    int? MaxTokens = null,
    double? Temperature = null);
