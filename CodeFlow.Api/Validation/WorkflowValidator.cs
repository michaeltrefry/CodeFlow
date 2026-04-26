using CodeFlow.Api.Dtos;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
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

    /// <summary>Implicit error-sink port present on every node. Authors cannot declare it.</summary>
    internal const string ImplicitFailedPort = "Failed";

    /// <summary>Synthesized port emitted by ReviewLoop nodes when the round budget is exhausted.</summary>
    internal const string ReviewLoopExhaustedPort = "Exhausted";

    /// <summary>Default loopDecision used by ReviewLoop when an author has not overridden it.</summary>
    internal const string DefaultLoopDecisionPort = "Rejected";

    public static async Task<ValidationResult> ValidateAsync(
        string key,
        string? name,
        int? maxRoundsPerRound,
        IReadOnlyList<WorkflowNodeDto>? nodes,
        IReadOnlyList<WorkflowEdgeDto>? edges,
        IReadOnlyList<WorkflowInputDto>? inputs,
        CodeFlowDbContext dbContext,
        IWorkflowRepository workflowRepository,
        IAgentConfigRepository agentRepository,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(workflowRepository);
        ArgumentNullException.ThrowIfNull(agentRepository);

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

        // Per-node syntactic validation. Authors must not declare reserved port names ("Failed"
        // is implicit on every node; "Exhausted" is reserved for ReviewLoop's synthesized port).
        foreach (var node in nodes)
        {
            var declaredPortViolation = CheckDeclaredPortReservations(node);
            if (declaredPortViolation is not null)
            {
                return ValidationResult.Fail(declaredPortViolation);
            }

            switch (node.Kind)
            {
                case WorkflowNodeKind.Start:
                case WorkflowNodeKind.Agent:
                case WorkflowNodeKind.Hitl:
                    if (string.IsNullOrWhiteSpace(node.AgentKey))
                    {
                        return ValidationResult.Fail($"Node {node.Id} of kind {node.Kind} must reference an AgentKey.");
                    }
                    break;

                case WorkflowNodeKind.Logic:
                    if (string.IsNullOrWhiteSpace(node.OutputScript))
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
                        if (string.Equals(loopDecision, ImplicitFailedPort, StringComparison.Ordinal))
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

        // Resolve agent declared outputs and child terminal ports for cross-validation.
        var agentOutputs = await ResolveAgentOutputsAsync(
            nodes, agentRepository, cancellationToken);

        var crossValidation = CrossValidateAgentPorts(nodes, agentOutputs);
        if (crossValidation is not null)
        {
            return ValidationResult.Fail(crossValidation);
        }

        var childTerminals = await ResolveChildTerminalPortsAsync(
            subflowReferenceNodes, dbContext, workflowRepository, cancellationToken);

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

            // Implicit Failed is always allowed as an edge source port, regardless of declarations.
            if (string.Equals(edge.FromPort, ImplicitFailedPort, StringComparison.Ordinal))
            {
                continue;
            }

            var allowed = AllowedOutputPorts(fromNode, childTerminals);
            if (!allowed.Contains(edge.FromPort, StringComparer.Ordinal))
            {
                return ValidationResult.Fail(
                    $"Edge from {DescribeNode(fromNode)} uses port '{edge.FromPort}' which is not declared. "
                    + $"Allowed ports: {FormatAllowedPorts(allowed)}.");
            }
        }

        var duplicateOutgoing = edges
            .GroupBy(e => (e.FromNodeId, e.FromPort))
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicateOutgoing is not null)
        {
            var duplicateNode = nodesById[duplicateOutgoing.Key.FromNodeId];
            return ValidationResult.Fail(
                $"Multiple edges leave {DescribeNode(duplicateNode)} on port '{duplicateOutgoing.Key.FromPort}'.");
        }

        var inputValidation = ValidateInputs(inputs);
        if (!inputValidation.IsValid)
        {
            return inputValidation;
        }

        return ValidationResult.Ok();
    }

    /// <summary>
    /// Canonical input key for the code-aware-workflow repos convention. When a workflow declares
    /// an input with this key (Kind=Json), its <c>DefaultValueJson</c> must be an array of
    /// <c>{url, branch?}</c> objects with non-empty url per entry. Validating at save time keeps
    /// authors from shipping a malformed template that the setup agent would otherwise reject at
    /// runtime with a less actionable error.
    /// </summary>
    internal const string RepositoriesInputKey = "repositories";

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
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(input.DefaultValueJson);
                }
                catch (JsonException)
                {
                    return ValidationResult.Fail(
                        $"Workflow input '{input.Key}' has a malformed DefaultValueJson.");
                }

                using (document)
                {
                    if (string.Equals(input.Key.Trim(), RepositoriesInputKey, StringComparison.Ordinal)
                        && input.Kind == WorkflowInputKind.Json)
                    {
                        var shapeError = ValidateRepositoriesShape(document.RootElement);
                        if (shapeError is not null)
                        {
                            return ValidationResult.Fail(
                                $"Workflow input 'repositories' has an invalid shape: {shapeError}");
                        }
                    }
                }
            }
        }

        return ValidationResult.Ok();
    }

    private static string? ValidateRepositoriesShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return $"expected an array of {{url, branch?}} entries, got {root.ValueKind}.";
        }

        var index = 0;
        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                return $"entry [{index}] must be an object with at least a 'url' string.";
            }

            if (!entry.TryGetProperty("url", out var urlElement)
                || urlElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(urlElement.GetString()))
            {
                return $"entry [{index}] is missing a non-empty 'url' string.";
            }

            if (entry.TryGetProperty("branch", out var branchElement)
                && branchElement.ValueKind != JsonValueKind.String
                && branchElement.ValueKind != JsonValueKind.Null)
            {
                return $"entry [{index}] 'branch' must be a string when present.";
            }

            index++;
        }

        return null;
    }

    /// <summary>
    /// Reject reserved port names in a node's declared <see cref="WorkflowNodeDto.OutputPorts"/>:
    /// <c>Failed</c> is implicit on every node and never declared explicitly; <c>Exhausted</c> is
    /// reserved for ReviewLoop's synthesized port and must not appear on any node's declared list.
    /// </summary>
    private static string? CheckDeclaredPortReservations(WorkflowNodeDto node)
    {
        if (node.OutputPorts is null)
        {
            return null;
        }

        foreach (var port in node.OutputPorts)
        {
            if (string.IsNullOrWhiteSpace(port))
            {
                continue;
            }

            if (string.Equals(port, ImplicitFailedPort, StringComparison.Ordinal))
            {
                return $"Node {node.Id} of kind {node.Kind} declares the implicit '{ImplicitFailedPort}' port "
                    + "in outputPorts. The Failed port is implicit on every node and must not be declared.";
            }

            if (string.Equals(port, ReviewLoopExhaustedPort, StringComparison.Ordinal))
            {
                return $"Node {node.Id} of kind {node.Kind} declares the reserved '{ReviewLoopExhaustedPort}' port "
                    + "in outputPorts. The Exhausted port is reserved for ReviewLoop synthesis "
                    + "and must not be declared on any node.";
            }
        }

        return null;
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyCollection<string>>> ResolveAgentOutputsAsync(
        IReadOnlyList<WorkflowNodeDto> nodes,
        IAgentConfigRepository agentRepository,
        CancellationToken cancellationToken)
    {
        // Cache by (key, resolved-version) so duplicate references share one repo lookup.
        var byNodeId = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        var byVersionKey = new Dictionary<(string Key, int Version), IReadOnlyCollection<string>>();

        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.AgentKey))
            {
                continue;
            }

            var nodeId = node.Id.ToString();
            if (byNodeId.ContainsKey(nodeId))
            {
                continue;
            }

            int version;
            try
            {
                version = node.AgentVersion ?? await agentRepository.GetLatestVersionAsync(
                    node.AgentKey.Trim(), cancellationToken);
            }
            catch (AgentConfigNotFoundException)
            {
                // Already surfaced as a "Workflow references unknown agent(s)" error earlier.
                continue;
            }

            var versionKey = (node.AgentKey.Trim(), version);
            if (!byVersionKey.TryGetValue(versionKey, out var ports))
            {
                AgentConfig agentConfig;
                try
                {
                    agentConfig = await agentRepository.GetAsync(versionKey.Item1, version, cancellationToken);
                }
                catch (AgentConfigNotFoundException)
                {
                    continue;
                }

                ports = agentConfig.DeclaredOutputs
                    .Select(o => o.Kind)
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                byVersionKey[versionKey] = ports;
            }

            byNodeId[nodeId] = ports;
        }

        return byNodeId;
    }

    private static string? CrossValidateAgentPorts(
        IReadOnlyList<WorkflowNodeDto> nodes,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> agentOutputsByNodeId)
    {
        foreach (var node in nodes)
        {
            if (node.Kind is not WorkflowNodeKind.Start
                and not WorkflowNodeKind.Agent
                and not WorkflowNodeKind.Hitl)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(node.AgentKey))
            {
                continue;
            }

            if (!agentOutputsByNodeId.TryGetValue(node.Id.ToString(), out var declared))
            {
                continue;
            }

            var nodePorts = node.OutputPorts ?? Array.Empty<string>();
            var undeclared = nodePorts
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Where(p => !string.Equals(p, ImplicitFailedPort, StringComparison.Ordinal))
                .Where(p => !declared.Contains(p, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (undeclared.Length > 0)
            {
                return $"{DescribeNode(node)} declares output port(s) {FormatPortList(undeclared)} "
                    + $"that are not in agent '{node.AgentKey}' declared outputs "
                    + $"({FormatPortList(declared)}). The agent must declare every routed port.";
            }
        }

        return null;
    }

    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>> ResolveChildTerminalPortsAsync(
        IReadOnlyList<WorkflowNodeDto> subflowReferenceNodes,
        CodeFlowDbContext dbContext,
        IWorkflowRepository workflowRepository,
        CancellationToken cancellationToken)
    {
        if (subflowReferenceNodes.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyCollection<string>>();
        }

        // For nodes pinning a null version, resolve the latest version in one batched query so we
        // can call GetTerminalPortsAsync per (key, version) afterward.
        var keysNeedingLatest = subflowReferenceNodes
            .Where(n => n.SubflowVersion is null)
            .Select(n => n.SubflowKey!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var latestByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        if (keysNeedingLatest.Length > 0)
        {
            var rows = await dbContext.Workflows
                .AsNoTracking()
                .Where(w => keysNeedingLatest.Contains(w.Key))
                .GroupBy(w => w.Key)
                .Select(g => new { Key = g.Key, Latest = g.Max(w => w.Version) })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                latestByKey[row.Key] = row.Latest;
            }
        }

        var byNodeId = new Dictionary<Guid, IReadOnlyCollection<string>>();
        var byVersionKey = new Dictionary<(string Key, int Version), IReadOnlyCollection<string>>();

        foreach (var node in subflowReferenceNodes)
        {
            var subflowKey = node.SubflowKey!.Trim();
            int version;
            if (node.SubflowVersion is int pinned)
            {
                version = pinned;
            }
            else if (latestByKey.TryGetValue(subflowKey, out var latest))
            {
                version = latest;
            }
            else
            {
                continue;
            }

            var versionKey = (subflowKey, version);
            if (!byVersionKey.TryGetValue(versionKey, out var ports))
            {
                try
                {
                    ports = await workflowRepository.GetTerminalPortsAsync(subflowKey, version, cancellationToken);
                }
                catch (WorkflowNotFoundException)
                {
                    continue;
                }

                byVersionKey[versionKey] = ports;
            }

            byNodeId[node.Id] = ports;
        }

        return byNodeId;
    }

    /// <summary>
    /// Format a node for use in validation error messages — kind plus a recognizable identifier
    /// (agent key for agent-bearing nodes, subflow key for Subflow/ReviewLoop, raw id otherwise).
    /// The raw id is appended in parens so authors can still locate the node by id when needed.
    /// </summary>
    private static string DescribeNode(WorkflowNodeDto node)
    {
        var label = node.Kind switch
        {
            WorkflowNodeKind.Start or WorkflowNodeKind.Agent or WorkflowNodeKind.Hitl
                when !string.IsNullOrWhiteSpace(node.AgentKey) => $"{node.Kind} '{node.AgentKey}'",
            WorkflowNodeKind.Subflow or WorkflowNodeKind.ReviewLoop
                when !string.IsNullOrWhiteSpace(node.SubflowKey) => $"{node.Kind} '{node.SubflowKey}'",
            _ => node.Kind.ToString()
        };
        return $"{label} ({node.Id})";
    }

    /// <summary>
    /// Allowed source-port set for an edge leaving the given node. The implicit
    /// <see cref="ImplicitFailedPort"/> is always allowed and is excluded from this listing — the
    /// edge-validation loop short-circuits Failed before consulting the set, so callers see a
    /// declared-port view here for use in error messages.
    /// </summary>
    internal static IReadOnlyCollection<string> AllowedOutputPorts(
        WorkflowNodeDto node,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>? childTerminals = null)
    {
        switch (node.Kind)
        {
            case WorkflowNodeKind.Start:
            case WorkflowNodeKind.Agent:
            case WorkflowNodeKind.Hitl:
            case WorkflowNodeKind.Logic:
                return node.OutputPorts is { Count: > 0 } declared
                    ? declared.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToArray()
                    : Array.Empty<string>();

            case WorkflowNodeKind.Subflow:
                {
                    var terminals = childTerminals is not null && childTerminals.TryGetValue(node.Id, out var t)
                        ? t
                        : (IReadOnlyCollection<string>)Array.Empty<string>();
                    return terminals;
                }

            case WorkflowNodeKind.ReviewLoop:
                {
                    var terminals = childTerminals is not null && childTerminals.TryGetValue(node.Id, out var t)
                        ? t
                        : (IReadOnlyCollection<string>)Array.Empty<string>();
                    var loopDecision = string.IsNullOrWhiteSpace(node.LoopDecision)
                        ? DefaultLoopDecisionPort
                        : node.LoopDecision.Trim();
                    var set = new HashSet<string>(terminals, StringComparer.Ordinal)
                    {
                        ReviewLoopExhaustedPort,
                        loopDecision,
                    };
                    return set;
                }

            default:
                return Array.Empty<string>();
        }
    }

    private static string FormatAllowedPorts(IReadOnlyCollection<string> allowed)
    {
        if (allowed.Count == 0)
        {
            return $"(none declared); '{ImplicitFailedPort}' is always implicitly available";
        }
        return string.Join(", ", allowed.OrderBy(p => p, StringComparer.Ordinal))
            + $", '{ImplicitFailedPort}' (implicit)";
    }

    private static string FormatPortList(IEnumerable<string> ports)
    {
        var ordered = ports
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        return ordered.Length == 0 ? "(none)" : "[" + string.Join(", ", ordered) + "]";
    }
}
