using CodeFlow.Api.Dtos;
using CodeFlow.Persistence;

namespace CodeFlow.Api.Validation.Pipeline.Rules;

/// <summary>
/// V4: Diff each agent-pinned node's <see cref="WorkflowNodeDto.OutputPorts"/> against the
/// pinned <see cref="AgentConfig.DeclaredOutputs"/>. Catches the class of "workflow completes
/// without reaching the right port" runtime failures by surfacing port mismatches at save time.
///
/// Two findings per mismatch direction:
/// <list type="bullet">
///   <item><description><b>Error</b> — node wires a port the agent cannot submit on (agent
///   would never reach that branch at runtime). Blocks save.</description></item>
///   <item><description><b>Warning</b> — agent declares a port the node leaves unwired (dead
///   branch from the workflow's perspective). Allowed but visible.</description></item>
/// </list>
///
/// The implicit <c>Failed</c> port is excluded from the wired-but-undeclared check (every node
/// has it implicitly). Subflow / ReviewLoop nodes are skipped — their ports come from the child
/// workflow, not from a pinned agent.
/// </summary>
public sealed class PortCouplingRule : IWorkflowValidationRule
{
    private const string ImplicitFailedPort = "Failed";

    public string RuleId => "port-coupling";

    public int Order => 200;

    public async Task<IReadOnlyList<WorkflowValidationFinding>> RunAsync(
        WorkflowValidationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<WorkflowValidationFinding>();
        // Cache by (key, resolved-version) so duplicate references share one repo lookup. Same
        // shape as the legacy WorkflowValidator's helper to keep perf comparable on big graphs.
        var declaredByVersion = new Dictionary<(string Key, int Version), IReadOnlyCollection<string>>();

        foreach (var node in context.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsAgentPinnedNodeKind(node.Kind))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(node.AgentKey))
            {
                continue;
            }

            var agentKey = node.AgentKey.Trim();

            int version;
            try
            {
                version = node.AgentVersion ?? await context.AgentRepository
                    .GetLatestVersionAsync(agentKey, cancellationToken);
            }
            catch (AgentConfigNotFoundException)
            {
                // Surfaced separately (legacy validator catches "unknown agent"); skipping the
                // port check here avoids piling on with a less-actionable mismatch message.
                continue;
            }

            var versionKey = (agentKey, version);
            if (!declaredByVersion.TryGetValue(versionKey, out var declared))
            {
                AgentConfig agentConfig;
                try
                {
                    agentConfig = await context.AgentRepository.GetAsync(
                        agentKey, version, cancellationToken);
                }
                catch (AgentConfigNotFoundException)
                {
                    continue;
                }

                declared = agentConfig.DeclaredOutputs
                    .Select(o => o.Kind)
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                declaredByVersion[versionKey] = declared;
            }

            var nodePorts = (node.OutputPorts ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            // Error: wired but agent can't submit. Implicit Failed is always submittable, exclude.
            foreach (var wired in nodePorts)
            {
                if (string.Equals(wired, ImplicitFailedPort, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!declared.Contains(wired, StringComparer.Ordinal))
                {
                    findings.Add(new WorkflowValidationFinding(
                        RuleId: RuleId,
                        Severity: WorkflowValidationSeverity.Error,
                        Message: $"Node wires output port '{wired}' but agent '{agentKey}' "
                            + $"v{version} does not declare it. The agent cannot submit on this "
                            + "port — the branch is unreachable at runtime. Either declare the "
                            + "port on the agent or remove the wiring.",
                        Location: new WorkflowValidationLocation(NodeId: node.Id)));
                }
            }

            // Warning: agent declares a port nothing wires. Dead branch.
            foreach (var declaredPort in declared)
            {
                if (!nodePorts.Contains(declaredPort, StringComparer.Ordinal))
                {
                    findings.Add(new WorkflowValidationFinding(
                        RuleId: RuleId,
                        Severity: WorkflowValidationSeverity.Warning,
                        Message: $"Agent '{agentKey}' v{version} declares output port "
                            + $"'{declaredPort}' but the node does not wire it. The agent can "
                            + "submit on this port at runtime but the workflow has no edge for "
                            + "it — the trace will exit through the implicit Failed port.",
                        Location: new WorkflowValidationLocation(NodeId: node.Id)));
                }
            }
        }

        return findings;
    }

    private static bool IsAgentPinnedNodeKind(WorkflowNodeKind kind) =>
        kind is WorkflowNodeKind.Start or WorkflowNodeKind.Agent or WorkflowNodeKind.Hitl;
}
