using CodeFlow.Contracts;
using CodeFlow.Runtime;

namespace CodeFlow.Orchestration;

/// <summary>
/// Epic 993 / NO-5: shared merge logic for applying per-node agent overrides to both the
/// invocation config and budget. Called by both the message-threaded path
/// (<see cref="AgentInvocationConsumer"/>) and the in-process path
/// (<see cref="NodeDispatch.GoalNodeDispatcher"/>).
/// </summary>
public static class AgentOverrideMerge
{
    /// <summary>
    /// Apply per-node overrides to the agent's invocation configuration. Overrides the provider,
    /// model, and MaxTokens when specified; leaves fields null-by-default (inherit-from-agent).
    /// </summary>
    public static AgentInvocationConfiguration MergeConfiguration(
        AgentInvocationConfiguration original,
        AgentInvocationOverrides? overrides)
    {
        if (overrides is null)
        {
            return original;
        }

        var provider = overrides.ModelProvider ?? original.Provider;
        var model = overrides.Model ?? original.Model;

        return original with
        {
            Provider = provider,
            Model = model,
            MaxTokens = overrides.MaxOutputTokens ?? original.MaxTokens,
            Budget = MergeBudget(original.Budget, overrides),
        };
    }

    /// <summary>
    /// Apply per-node budget overrides to the agent's invocation loop budget.
    /// </summary>
    private static InvocationLoopBudget? MergeBudget(
        InvocationLoopBudget? original,
        AgentInvocationOverrides overrides)
    {
        // If the agent has no budget and there are no overrides, return null (inherit default).
        if (original is null && !HasBudgetOverrides(overrides))
        {
            return null;
        }

        // Start with the agent's budget or the default.
        var budget = original ?? InvocationLoopBudget.Default;

        // Apply overrides field-by-field.
        return budget with
        {
            MaxToolCalls = overrides.MaxToolCalls ?? budget.MaxToolCalls,
            MaxLoopDuration = overrides.MaxLoopDurationSeconds.HasValue
                ? TimeSpan.FromSeconds(overrides.MaxLoopDurationSeconds.Value)
                : budget.MaxLoopDuration,
            MaxConsecutiveNonMutatingCalls = overrides.MaxConsecutiveNonMutatingCalls
                ?? budget.MaxConsecutiveNonMutatingCalls,
        };
    }

    private static bool HasBudgetOverrides(AgentInvocationOverrides overrides) =>
        overrides.MaxToolCalls.HasValue ||
        overrides.MaxLoopDurationSeconds.HasValue ||
        overrides.MaxConsecutiveNonMutatingCalls.HasValue;
}
