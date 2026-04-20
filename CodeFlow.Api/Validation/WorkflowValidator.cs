using CodeFlow.Api.Dtos;
using CodeFlow.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Validation;

public static class WorkflowValidator
{
    public const int MinRoundsPerRound = 1;
    public const int MaxRoundsPerRoundUpperBound = 50;

    public static async Task<ValidationResult> ValidateAsync(
        string key,
        string? name,
        string? startAgentKey,
        string? escalationAgentKey,
        int? maxRoundsPerRound,
        IReadOnlyList<WorkflowEdgeDto>? edges,
        CodeFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var keyValidation = AgentConfigValidator.ValidateKey(key);
        if (!keyValidation.IsValid)
        {
            return keyValidation;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return ValidationResult.Fail("Workflow name must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(startAgentKey))
        {
            return ValidationResult.Fail("Workflow must reference a start agent.");
        }

        var rounds = maxRoundsPerRound ?? 3;
        if (rounds < MinRoundsPerRound || rounds > MaxRoundsPerRoundUpperBound)
        {
            return ValidationResult.Fail(
                $"maxRoundsPerRound must be between {MinRoundsPerRound} and {MaxRoundsPerRoundUpperBound}.");
        }

        if (edges is null || edges.Count == 0)
        {
            return ValidationResult.Fail("Workflow must include at least one edge.");
        }

        var agentKeys = edges
            .SelectMany(edge => new[] { edge.FromAgentKey, edge.ToAgentKey })
            .Concat(new[] { startAgentKey! })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(escalationAgentKey))
        {
            agentKeys = agentKeys
                .Append(escalationAgentKey!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var known = await dbContext.Agents
            .AsNoTracking()
            .Where(agent => agentKeys.Contains(agent.Key))
            .Select(agent => agent.Key)
            .Distinct()
            .ToListAsync(cancellationToken);

        var missing = agentKeys
            .Where(agent => !known.Contains(agent, StringComparer.Ordinal))
            .ToArray();

        if (missing.Length > 0)
        {
            return ValidationResult.Fail(
                $"Workflow references unknown agent(s): {string.Join(", ", missing)}.");
        }

        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startAgentKey! };
        var added = true;
        while (added)
        {
            added = false;
            foreach (var edge in edges)
            {
                if (reachable.Contains(edge.FromAgentKey) && reachable.Add(edge.ToAgentKey))
                {
                    added = true;
                }
            }
        }

        var unreachable = edges
            .Select(edge => edge.FromAgentKey)
            .Concat(edges.Select(edge => edge.ToAgentKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(agent => !reachable.Contains(agent))
            .ToArray();

        if (unreachable.Length > 0)
        {
            return ValidationResult.Fail(
                $"Workflow contains unreachable agent(s) from start: {string.Join(", ", unreachable)}.");
        }

        return ValidationResult.Ok();
    }
}
