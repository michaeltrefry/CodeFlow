using CodeFlow.Api.Dtos;
using CodeFlow.Persistence;
using CodeFlow.Runtime;

namespace CodeFlow.Api.Validation.Pipeline.Rules;

/// <summary>
/// P4 + P5: surface workflow-node configurations that would target a framework-managed
/// reserved variable namespace (today, <c>__loop.*</c>). The runtime silently skips these
/// writes — but failing fast at save time tells the author exactly which field is wrong
/// instead of producing a confusing "the variable never appears" trace later.
///
/// Findings:
/// <list type="bullet">
/// <item><description><b>Error</b> on <see cref="WorkflowNodeDto.MirrorOutputToWorkflowVar"/>
/// targeting a reserved key — the agent's output would be silently dropped on the floor.</description></item>
/// <item><description><b>Error</b> on any port-replacement value
/// (<see cref="WorkflowNodeDto.OutputPortReplacements"/>) targeting a reserved key — the
/// runtime would refuse to read and the downstream artifact would fall back to the agent's
/// original submission.</description></item>
/// </list>
/// </summary>
public sealed class ProtectedVariableTargetRule : IWorkflowValidationRule
{
    public string RuleId => "protected-variable-target";

    public int Order => 240;

    public Task<IReadOnlyList<WorkflowValidationFinding>> RunAsync(
        WorkflowValidationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<WorkflowValidationFinding>();

        foreach (var node in context.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mirrorTarget = node.MirrorOutputToWorkflowVar?.Trim();
            if (!string.IsNullOrEmpty(mirrorTarget) && ProtectedVariables.IsReserved(mirrorTarget))
            {
                findings.Add(new WorkflowValidationFinding(
                    RuleId: RuleId,
                    Severity: WorkflowValidationSeverity.Error,
                    Message: $"Node mirrors output to '{mirrorTarget}', a framework-managed "
                        + "workflow variable. Pick a non-reserved key — the runtime refuses "
                        + "to clobber values in the __loop.* / workDir / traceId namespaces.",
                    Location: new WorkflowValidationLocation(NodeId: node.Id)));
            }

            if (node.OutputPortReplacements is { Count: > 0 } replacements)
            {
                foreach (var (port, key) in replacements)
                {
                    var trimmedKey = key?.Trim();
                    if (string.IsNullOrEmpty(trimmedKey))
                    {
                        continue;
                    }

                    if (ProtectedVariables.IsReserved(trimmedKey))
                    {
                        findings.Add(new WorkflowValidationFinding(
                            RuleId: RuleId,
                            Severity: WorkflowValidationSeverity.Error,
                            Message: $"Port '{port}' replaces its artifact from workflow "
                                + $"variable '{trimmedKey}', a framework-managed name. Pick a "
                                + "non-reserved key — these values are owned by the runtime and "
                                + "cannot be referenced as artifact replacements.",
                            Location: new WorkflowValidationLocation(NodeId: node.Id)));
                    }
                }
            }
        }

        return Task.FromResult<IReadOnlyList<WorkflowValidationFinding>>(findings);
    }
}
