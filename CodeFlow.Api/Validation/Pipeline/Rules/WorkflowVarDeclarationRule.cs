using System.Text.RegularExpressions;
using CodeFlow.Api.Dtos;
using CodeFlow.Orchestration.Scripting;
using CodeFlow.Persistence;

namespace CodeFlow.Api.Validation.Pipeline.Rules;

/// <summary>
/// VZ2 (Workflow Authoring DX): when a workflow declares <c>WorkflowVarsReads</c> /
/// <c>WorkflowVarsWrites</c>, lint reachable agent prompts and node scripts against the
/// declarations.
///
/// Findings (all warnings — VZ2 is an opt-in linting layer, never a blocking error):
/// <list type="bullet">
///   <item><description>An agent's prompt references <c>{{ workflow.X }}</c> but X is not in
///   <c>WorkflowVarsReads</c> AND no upstream node writes X — the prompt would render a
///   literal token at runtime.</description></item>
///   <item><description>An upstream node writes <c>setWorkflow('X', ...)</c> (or the framework
///   features mirror / rejection-history target X) but X is not in <c>WorkflowVarsWrites</c> —
///   undeclared write.</description></item>
/// </list>
///
/// Skips entirely when both lists are NULL (the author hasn't opted in). An empty list (the
/// author explicitly declared "reads/writes nothing") still runs the validator — handy for
/// tightening the contract on a workflow.
///
/// The rule consumes <see cref="IWorkflowDataflowAnalyzer"/> for upstream-write detection,
/// so it operates on the same analysis as the editor's data-flow inspector.
/// </summary>
public sealed class WorkflowVarDeclarationRule : IWorkflowValidationRule
{
    /// <summary>
    /// Captures every <c>{{ workflow.X }}</c> / <c>{{ workflow.X.y }}</c> reference in a
    /// prompt template. The first dotted segment after <c>workflow.</c> is the variable
    /// name; nested paths (<c>workflow.X.y.z</c>) are still keyed by the top-level name.
    /// Reserved framework prefixes (today, <c>__loop</c>) are filtered out — they're never
    /// declarable by the author.
    /// </summary>
    private static readonly Regex WorkflowReferencePattern = new(
        @"\{\{\s*workflow\.([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IWorkflowDataflowAnalyzer dataflowAnalyzer;

    public WorkflowVarDeclarationRule(IWorkflowDataflowAnalyzer dataflowAnalyzer)
    {
        this.dataflowAnalyzer = dataflowAnalyzer ?? throw new ArgumentNullException(nameof(dataflowAnalyzer));
    }

    public string RuleId => "workflow-vars-declaration";

    public int Order => 250;

    public async Task<IReadOnlyList<WorkflowValidationFinding>> RunAsync(
        WorkflowValidationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var declaredReads = context.WorkflowVarsReads;
        var declaredWrites = context.WorkflowVarsWrites;

        // Skip entirely when the author hasn't opted in. NULL = absence of declaration; empty
        // array = explicit empty declaration (still runs).
        if (declaredReads is null && declaredWrites is null)
        {
            return Array.Empty<WorkflowValidationFinding>();
        }

        var findings = new List<WorkflowValidationFinding>();
        var workflow = await TryBuildWorkflowAsync(context, cancellationToken);
        if (workflow is null)
        {
            return findings;
        }

        var snapshot = dataflowAnalyzer.Analyze(workflow);

        if (declaredWrites is not null)
        {
            CheckWrites(workflow, declaredWrites, findings);
        }

        if (declaredReads is not null)
        {
            await CheckReadsAsync(context, workflow, declaredReads, snapshot, findings, cancellationToken);
        }

        return findings;
    }

    private static void CheckWrites(
        Workflow workflow,
        IReadOnlyList<string> declaredWrites,
        List<WorkflowValidationFinding> findings)
    {
        var declaredSet = new HashSet<string>(declaredWrites, StringComparer.Ordinal);

        foreach (var node in workflow.Nodes)
        {
            // P3: a ReviewLoop with rejection-history enabled writes __loop.rejectionHistory,
            // which is reserved — author never declares it. Skip the framework key.
            if (node.Kind == WorkflowNodeKind.ReviewLoop
                && node.RejectionHistory is { Enabled: true })
            {
                // The framework write is deliberate; nothing to flag.
            }

            // P4: mirror writes the configured key. If the author declared writes but didn't
            // include the mirror target, surface a warning.
            if (!string.IsNullOrWhiteSpace(node.MirrorOutputToWorkflowVar))
            {
                var mirrorKey = node.MirrorOutputToWorkflowVar.Trim();
                if (!IsReserved(mirrorKey) && !declaredSet.Contains(mirrorKey))
                {
                    findings.Add(new WorkflowValidationFinding(
                        RuleId: "workflow-vars-declaration",
                        Severity: WorkflowValidationSeverity.Warning,
                        Message: $"Node mirrors output to workflow variable '{mirrorKey}', "
                            + "but the workflow's WorkflowVarsWrites declaration does not list "
                            + "this key. Add it to the declaration or remove the mirror.",
                        Location: new WorkflowValidationLocation(NodeId: node.Id)));
                }
            }

            // Output / input scripts: parsed by the F2 extractor. Drive the same analysis here
            // so script-driven writes are flagged consistently with mirror writes.
            var inputResult = ScriptDataflowExtractor.Extract(node.InputScript, "input");
            var outputResult = ScriptDataflowExtractor.Extract(node.OutputScript, "output");

            foreach (var write in inputResult.WorkflowWrites.Concat(outputResult.WorkflowWrites))
            {
                var key = write.Key;
                if (IsReserved(key) || declaredSet.Contains(key))
                {
                    continue;
                }

                findings.Add(new WorkflowValidationFinding(
                    RuleId: "workflow-vars-declaration",
                    Severity: WorkflowValidationSeverity.Warning,
                    Message: $"Script on node writes workflow variable '{key}' via "
                        + "setWorkflow(), but the workflow's WorkflowVarsWrites declaration "
                        + "does not list this key. Add it to the declaration or remove the write.",
                    Location: new WorkflowValidationLocation(NodeId: node.Id)));
            }
        }
    }

    private async Task CheckReadsAsync(
        WorkflowValidationContext context,
        Workflow workflow,
        IReadOnlyList<string> declaredReads,
        WorkflowDataflowSnapshot snapshot,
        List<WorkflowValidationFinding> findings,
        CancellationToken cancellationToken)
    {
        var declaredSet = new HashSet<string>(declaredReads, StringComparer.Ordinal);

        foreach (var node in workflow.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(node.AgentKey))
            {
                continue;
            }

            int agentVersion;
            try
            {
                agentVersion = node.AgentVersion ?? await context.AgentRepository
                    .GetLatestVersionAsync(node.AgentKey, cancellationToken);
            }
            catch (AgentConfigNotFoundException)
            {
                continue;
            }

            AgentConfig agentConfig;
            try
            {
                agentConfig = await context.AgentRepository
                    .GetAsync(node.AgentKey, agentVersion, cancellationToken);
            }
            catch (AgentConfigNotFoundException)
            {
                continue;
            }

            var systemPrompt = agentConfig.Configuration.SystemPrompt ?? string.Empty;
            var promptTemplate = agentConfig.Configuration.PromptTemplate ?? string.Empty;
            var referencedKeys = ExtractWorkflowReferences(systemPrompt)
                .Concat(ExtractWorkflowReferences(promptTemplate))
                .ToHashSet(StringComparer.Ordinal);

            if (referencedKeys.Count == 0)
            {
                continue;
            }

            var inScopeFromUpstream = snapshot.ScopesByNode.TryGetValue(node.Id, out var nodeScope)
                ? nodeScope.WorkflowVariables.Select(v => v.Key).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            foreach (var key in referencedKeys)
            {
                if (IsReserved(key))
                {
                    continue;
                }
                if (declaredSet.Contains(key))
                {
                    continue;
                }
                if (inScopeFromUpstream.Contains(key))
                {
                    continue;
                }

                findings.Add(new WorkflowValidationFinding(
                    RuleId: "workflow-vars-declaration",
                    Severity: WorkflowValidationSeverity.Warning,
                    Message: $"Agent '{node.AgentKey}' v{agentVersion} references "
                        + $"{{{{ workflow.{key} }}}} but no upstream node writes '{key}' AND "
                        + "the workflow's WorkflowVarsReads declaration does not list it. "
                        + "Add the variable to the declaration, or wire an upstream node that "
                        + "writes it.",
                    Location: new WorkflowValidationLocation(NodeId: node.Id)));
            }
        }
    }

    private static IEnumerable<string> ExtractWorkflowReferences(string template)
    {
        if (string.IsNullOrEmpty(template))
        {
            yield break;
        }

        foreach (Match match in WorkflowReferencePattern.Matches(template))
        {
            yield return match.Groups[1].Value;
        }
    }

    private static bool IsReserved(string key)
    {
        // Mirrors ProtectedVariables.ReservedKeys / ReservedNamespaces — these are
        // framework-managed and never appear in author declarations. The legacy `workDir`
        // alias was retired in sc-604; only `traceWorkDir` remains for the per-trace path.
        return key.StartsWith("__loop", StringComparison.Ordinal)
            || key.Equals("traceWorkDir", StringComparison.Ordinal)
            || key.Equals("traceId", StringComparison.Ordinal);
    }

    /// <summary>
    /// Build a <see cref="Workflow"/> from the validation context's DTOs so the F2 analyzer
    /// can chew on the same shape it would for a saved workflow. Returns null on bad input
    /// (the rule emits no findings — other rules surface the structural problem).
    /// </summary>
    private static Task<Workflow?> TryBuildWorkflowAsync(
        WorkflowValidationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var nodes = context.Nodes
                .Select(dto => new WorkflowNode(
                    Id: dto.Id,
                    Kind: dto.Kind,
                    AgentKey: dto.AgentKey,
                    AgentVersion: dto.AgentVersion,
                    OutputScript: dto.OutputScript,
                    OutputPorts: dto.OutputPorts ?? Array.Empty<string>(),
                    LayoutX: dto.LayoutX,
                    LayoutY: dto.LayoutY,
                    SubflowKey: dto.SubflowKey,
                    SubflowVersion: dto.SubflowVersion,
                    ReviewMaxRounds: dto.ReviewMaxRounds,
                    LoopDecision: dto.LoopDecision,
                    InputScript: dto.InputScript,
                    OptOutLastRoundReminder: dto.OptOutLastRoundReminder,
                    RejectionHistory: dto.RejectionHistory,
                    MirrorOutputToWorkflowVar: dto.MirrorOutputToWorkflowVar,
                    OutputPortReplacements: dto.OutputPortReplacements))
                .ToArray();
            var edges = context.Edges
                .Select(dto => new WorkflowEdge(
                    FromNodeId: dto.FromNodeId,
                    FromPort: dto.FromPort,
                    ToNodeId: dto.ToNodeId,
                    ToPort: string.IsNullOrWhiteSpace(dto.ToPort) ? WorkflowEdge.DefaultInputPort : dto.ToPort,
                    RotatesRound: dto.RotatesRound,
                    SortOrder: dto.SortOrder,
                    IntentionalBackedge: dto.IntentionalBackedge))
                .ToArray();
            var inputs = (context.Inputs ?? Array.Empty<WorkflowInputDto>())
                .Select(dto => new WorkflowInput(
                    Key: dto.Key,
                    DisplayName: dto.DisplayName,
                    Kind: dto.Kind,
                    Required: dto.Required,
                    DefaultValueJson: dto.DefaultValueJson,
                    Description: dto.Description,
                    Ordinal: dto.Ordinal))
                .ToArray();

            var workflow = new Workflow(
                Key: string.IsNullOrWhiteSpace(context.Key) ? "__draft" : context.Key,
                Version: 0,
                Name: context.Name ?? string.Empty,
                MaxRoundsPerRound: context.MaxRoundsPerRound ?? 3,
                CreatedAtUtc: DateTime.UtcNow,
                Nodes: nodes,
                Edges: edges,
                Inputs: inputs);
            return Task.FromResult<Workflow?>(workflow);
        }
        catch
        {
            return Task.FromResult<Workflow?>(null);
        }
    }
}
