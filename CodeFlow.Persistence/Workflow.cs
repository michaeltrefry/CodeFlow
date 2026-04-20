using CodeFlow.Runtime;
using System.Text.Json;

namespace CodeFlow.Persistence;

public sealed record Workflow(
    string Key,
    int Version,
    string Name,
    string StartAgentKey,
    string? EscalationAgentKey,
    int MaxRoundsPerRound,
    DateTime CreatedAtUtc,
    IReadOnlyList<WorkflowEdge> Edges)
{
    public WorkflowEdge? FindNext(
        string fromAgentKey,
        AgentDecision decision,
        JsonElement? discriminator = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return FindNext(fromAgentKey, decision.Kind, discriminator);
    }

    public WorkflowEdge? FindNext(
        string fromAgentKey,
        AgentDecisionKind decisionKind,
        JsonElement? discriminator = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromAgentKey);

        var normalizedFromAgentKey = fromAgentKey.Trim();

        var candidateEdges = Edges
            .Where(edge =>
                string.Equals(edge.FromAgentKey, normalizedFromAgentKey, StringComparison.OrdinalIgnoreCase) &&
                edge.Decision == decisionKind)
            .ToList();

        if (candidateEdges.Count == 0)
        {
            return null;
        }

        if (discriminator is not null)
        {
            var discriminatorSpecificMatch = candidateEdges
                .Where(edge => edge.Discriminator is not null &&
                    WorkflowJson.DeepEquals(edge.Discriminator, discriminator))
                .OrderBy(edge => edge.SortOrder)
                .FirstOrDefault();

            if (discriminatorSpecificMatch is not null)
            {
                return discriminatorSpecificMatch;
            }
        }

        return candidateEdges
            .Where(edge => edge.Discriminator is null)
            .OrderBy(edge => edge.SortOrder)
            .FirstOrDefault();
    }
}
