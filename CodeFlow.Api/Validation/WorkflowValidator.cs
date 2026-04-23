using CodeFlow.Api.Dtos;
using CodeFlow.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CodeFlow.Api.Validation;

public static class WorkflowValidator
{
    public const int MinRoundsPerRound = 1;
    public const int MaxRoundsPerRoundUpperBound = 50;

    /// <summary>Inclusive lower bound for a ReviewLoop node's MaxRounds.</summary>
    public const int MinReviewLoopMaxRounds = 1;
    /// <summary>Inclusive upper bound for a ReviewLoop node's MaxRounds. Picked to keep runaway
    /// loops from burning through a lot of LLM budget before the author notices.</summary>
    public const int MaxReviewLoopMaxRounds = 10;

    public static async Task<ValidationResult> ValidateAsync(
        string key,
        string? name,
        int? maxRoundsPerRound,
        IReadOnlyList<WorkflowNodeDto>? nodes,
        IReadOnlyList<WorkflowEdgeDto>? edges,
        IReadOnlyList<WorkflowInputDto>? inputs,
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

        var rounds = maxRoundsPerRound ?? 3;
        if (rounds < MinRoundsPerRound || rounds > MaxRoundsPerRoundUpperBound)
        {
            return ValidationResult.Fail(
                $"maxRoundsPerRound must be between {MinRoundsPerRound} and {MaxRoundsPerRoundUpperBound}.");
        }

        if (nodes is null || nodes.Count == 0)
        {
            return ValidationResult.Fail("Workflow must include at least one node.");
        }

        var nodesById = new Dictionary<Guid, WorkflowNodeDto>();
        foreach (var node in nodes)
        {
            if (node.Id == Guid.Empty)
            {
                return ValidationResult.Fail("Every workflow node must have a non-empty Id.");
            }
            if (!nodesById.TryAdd(node.Id, node))
            {
                return ValidationResult.Fail($"Duplicate node id: {node.Id}.");
            }
        }

        var startCount = nodes.Count(n => n.Kind == WorkflowNodeKind.Start);
        if (startCount != 1)
        {
            return ValidationResult.Fail("Workflow must declare exactly one Start node.");
        }

        var escalationCount = nodes.Count(n => n.Kind == WorkflowNodeKind.Escalation);
        if (escalationCount > 1)
        {
            return ValidationResult.Fail("Workflow may declare at most one Escalation node.");
        }

        foreach (var node in nodes)
        {
            switch (node.Kind)
            {
                case WorkflowNodeKind.Start:
                case WorkflowNodeKind.Agent:
                case WorkflowNodeKind.Hitl:
                case WorkflowNodeKind.Escalation:
                    if (string.IsNullOrWhiteSpace(node.AgentKey))
                    {
                        return ValidationResult.Fail($"Node {node.Id} of kind {node.Kind} must reference an AgentKey.");
                    }
                    break;

                case WorkflowNodeKind.Logic:
                    if (string.IsNullOrWhiteSpace(node.Script))
                    {
                        return ValidationResult.Fail($"Logic node {node.Id} must declare a non-empty script.");
                    }
                    if (node.OutputPorts is null || node.OutputPorts.Count == 0)
                    {
                        return ValidationResult.Fail($"Logic node {node.Id} must declare at least one output port.");
                    }
                    break;

                case WorkflowNodeKind.Subflow:
                    if (string.IsNullOrWhiteSpace(node.SubflowKey))
                    {
                        return ValidationResult.Fail(
                            $"Subflow node {node.Id} must reference a SubflowKey.");
                    }
                    if (string.Equals(node.SubflowKey!.Trim(), key.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return ValidationResult.Fail(
                            $"Subflow node {node.Id} points at its own workflow key '{key}'. "
                            + "Self-referential subflows are rejected at save time.");
                    }
                    break;

                case WorkflowNodeKind.ReviewLoop:
                    if (string.IsNullOrWhiteSpace(node.SubflowKey))
                    {
                        return ValidationResult.Fail(
                            $"ReviewLoop node {node.Id} must reference a SubflowKey.");
                    }
                    if (string.Equals(node.SubflowKey!.Trim(), key.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return ValidationResult.Fail(
                            $"ReviewLoop node {node.Id} points at its own workflow key '{key}'. "
                            + "Self-referential ReviewLoops are rejected at save time.");
                    }
                    if (node.ReviewMaxRounds is not int maxRounds)
                    {
                        return ValidationResult.Fail(
                            $"ReviewLoop node {node.Id} must set ReviewMaxRounds.");
                    }
                    if (maxRounds < MinReviewLoopMaxRounds || maxRounds > MaxReviewLoopMaxRounds)
                    {
                        return ValidationResult.Fail(
                            $"ReviewLoop node {node.Id} has ReviewMaxRounds = {maxRounds}, "
                            + $"which must be between {MinReviewLoopMaxRounds} and {MaxReviewLoopMaxRounds}.");
                    }
                    if (node.LoopDecision is not null)
                    {
                        var loopDecision = node.LoopDecision.Trim();
                        if (string.IsNullOrEmpty(loopDecision))
                        {
                            return ValidationResult.Fail(
                                $"ReviewLoop node {node.Id} LoopDecision, when set, must be a non-empty port name.");
                        }
                        if (loopDecision.Length > 64)
                        {
                            return ValidationResult.Fail(
                                $"ReviewLoop node {node.Id} LoopDecision '{loopDecision}' exceeds 64 characters.");
                        }
                        if (string.Equals(loopDecision, "Failed", StringComparison.Ordinal)
                            || string.Equals(loopDecision, "Escalated", StringComparison.Ordinal))
                        {
                            return ValidationResult.Fail(
                                $"ReviewLoop node {node.Id} LoopDecision cannot be '{loopDecision}' — "
                                + "that port name is reserved for error propagation.");
                        }
                    }
                    break;
            }
        }

        // Validate that referenced Subflow / ReviewLoop workflows exist (and the pinned version,
        // if any). Both node kinds point at another workflow via SubflowKey/SubflowVersion.
        var subflowReferenceNodes = nodes
            .Where(n => (n.Kind == WorkflowNodeKind.Subflow || n.Kind == WorkflowNodeKind.ReviewLoop)
                && !string.IsNullOrWhiteSpace(n.SubflowKey))
            .ToArray();

        if (subflowReferenceNodes.Length > 0)
        {
            var referencedKeys = subflowReferenceNodes
                .Select(n => n.SubflowKey!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var existingKeys = await dbContext.Workflows
                .AsNoTracking()
                .Where(w => referencedKeys.Contains(w.Key))
                .Select(w => w.Key)
                .Distinct()
                .ToListAsync(cancellationToken);

            var missingKeys = referencedKeys
                .Where(k => !existingKeys.Contains(k, StringComparer.Ordinal))
                .ToArray();

            if (missingKeys.Length > 0)
            {
                return ValidationResult.Fail(
                    $"Subflow/ReviewLoop node(s) reference unknown workflow key(s): {string.Join(", ", missingKeys)}.");
            }

            foreach (var node in subflowReferenceNodes)
            {
                if (node.SubflowVersion is not int pinnedVersion)
                {
                    continue; // null = "latest at save"; upstream resolver pins before persistence.
                }

                var versionExists = await dbContext.Workflows
                    .AsNoTracking()
                    .AnyAsync(
                        w => w.Key == node.SubflowKey!.Trim() && w.Version == pinnedVersion,
                        cancellationToken);

                if (!versionExists)
                {
                    return ValidationResult.Fail(
                        $"{node.Kind} node {node.Id} pins version {pinnedVersion} of workflow "
                        + $"'{node.SubflowKey}', but no such version exists.");
                }
            }
        }

        var agentKeyedNodes = nodes
            .Where(n => !string.IsNullOrWhiteSpace(n.AgentKey))
            .Select(n => n.AgentKey!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (agentKeyedNodes.Length > 0)
        {
            var known = await dbContext.Agents
                .AsNoTracking()
                .Where(agent => agentKeyedNodes.Contains(agent.Key))
                .Select(agent => agent.Key)
                .Distinct()
                .ToListAsync(cancellationToken);

            var missing = agentKeyedNodes
                .Where(agent => !known.Contains(agent, StringComparer.Ordinal))
                .ToArray();

            if (missing.Length > 0)
            {
                return ValidationResult.Fail(
                    $"Workflow references unknown agent(s): {string.Join(", ", missing)}.");
            }
        }

        edges ??= Array.Empty<WorkflowEdgeDto>();

        foreach (var edge in edges)
        {
            if (!nodesById.TryGetValue(edge.FromNodeId, out var fromNode))
            {
                return ValidationResult.Fail($"Edge references missing from-node {edge.FromNodeId}.");
            }
            if (!nodesById.TryGetValue(edge.ToNodeId, out _))
            {
                return ValidationResult.Fail($"Edge references missing to-node {edge.ToNodeId}.");
            }
            if (string.IsNullOrWhiteSpace(edge.FromPort))
            {
                return ValidationResult.Fail($"Edge from {edge.FromNodeId} must have a non-empty FromPort.");
            }
            if (fromNode.Kind == WorkflowNodeKind.Escalation)
            {
                return ValidationResult.Fail("Escalation node must not have outgoing edges.");
            }

            var nodePorts = AllowedOutputPorts(fromNode);
            if (nodePorts is not null && !nodePorts.Contains(edge.FromPort, StringComparer.Ordinal))
            {
                return ValidationResult.Fail(
                    $"Edge from node {fromNode.Id} uses port '{edge.FromPort}' which is not declared.");
            }
        }

        var duplicateOutgoing = edges
            .GroupBy(e => (e.FromNodeId, e.FromPort))
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicateOutgoing is not null)
        {
            return ValidationResult.Fail(
                $"Multiple edges leave node {duplicateOutgoing.Key.FromNodeId} on port '{duplicateOutgoing.Key.FromPort}'.");
        }

        var inputValidation = ValidateInputs(inputs);
        if (!inputValidation.IsValid)
        {
            return inputValidation;
        }

        return ValidationResult.Ok();
    }

    private static ValidationResult ValidateInputs(IReadOnlyList<WorkflowInputDto>? inputs)
    {
        if (inputs is null || inputs.Count == 0)
        {
            return ValidationResult.Ok();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Key))
            {
                return ValidationResult.Fail("Workflow input Key must not be empty.");
            }
            if (!seen.Add(input.Key.Trim()))
            {
                return ValidationResult.Fail($"Duplicate workflow input key '{input.Key}'.");
            }
            if (string.IsNullOrWhiteSpace(input.DisplayName))
            {
                return ValidationResult.Fail($"Workflow input '{input.Key}' must have a DisplayName.");
            }

            if (!string.IsNullOrWhiteSpace(input.DefaultValueJson))
            {
                try
                {
                    using var _ = JsonDocument.Parse(input.DefaultValueJson);
                }
                catch (JsonException)
                {
                    return ValidationResult.Fail(
                        $"Workflow input '{input.Key}' has a malformed DefaultValueJson.");
                }
            }
        }

        return ValidationResult.Ok();
    }

    /// <summary>
    /// The only port names a runtime Subflow node can emit. The child saga's terminal state is
    /// mapped 1:1 to one of these three ports by <c>RouteSubflowCompletionAsync</c>; edges
    /// wired from any other port name would never match at runtime, so we reject them at save.
    /// </summary>
    internal static readonly IReadOnlyCollection<string> SubflowAllowedPorts =
        new[] { "Completed", "Failed", "Escalated" };

    /// <summary>
    /// The only port names a runtime ReviewLoop node can emit. The child saga's terminal decision
    /// is mapped to one of these by <c>ResolveReviewLoopOutcome</c>; edges wired from any other
    /// port name would never match at runtime, so we reject them at save.
    /// </summary>
    internal static readonly IReadOnlyCollection<string> ReviewLoopAllowedPorts =
        new[] { "Approved", "Exhausted", "Failed" };

    private static IReadOnlyCollection<string>? AllowedOutputPorts(WorkflowNodeDto node)
    {
        return node.Kind switch
        {
            WorkflowNodeKind.Logic => node.OutputPorts?.ToArray() ?? Array.Empty<string>(),
            WorkflowNodeKind.Start or WorkflowNodeKind.Agent or WorkflowNodeKind.Hitl =>
                node.OutputPorts is { Count: > 0 } declared
                    ? declared.ToArray()
                    : null,
            WorkflowNodeKind.Subflow => SubflowAllowedPorts,
            WorkflowNodeKind.ReviewLoop => ReviewLoopAllowedPorts,
            _ => null
        };
    }
}
