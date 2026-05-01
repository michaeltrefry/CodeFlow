using System.Text.RegularExpressions;
using CodeFlow.Api.Dtos;
using CodeFlow.Persistence;
using CodeFlow.Runtime;

namespace CodeFlow.Api.Validation.Pipeline.Rules;

/// <summary>
/// V5: Flag agents missing role assignments. Without roles, an agent has no host tools and no
/// MCP grants, so any prompt that asks the agent to call tools will fail at runtime with
/// "no tools available."
///
/// Two findings:
/// <list type="bullet">
///   <item><description><b>Error</b> — agent has zero roles AND its prompt mentions a
///   host-tool / MCP capability. Save blocked.</description></item>
///   <item><description><b>Warning</b> — agent has zero roles regardless of prompt content.
///   Save allowed (pure-text agents like classifiers and PRD producers legitimately use no
///   tools).</description></item>
/// </list>
/// </summary>
public sealed class RoleAssignmentRule : IWorkflowValidationRule
{
    /// <summary>
    /// Host-tool / MCP capability tokens whose presence in a prompt implies the agent intends
    /// to call a host or MCP tool. <c>echo</c> and <c>now</c> are excluded — too generic and
    /// commonly appear in plain prose.
    /// </summary>
    private static readonly Regex CapabilityRegex = new(
        @"\b(read_file|apply_patch|run_command|vcs\.open_pr|vcs\.get_repo|container\.run|mcp:[A-Za-z0-9_\-]+)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string RuleId => "missing-role";

    public int Order => 210;

    public async Task<IReadOnlyList<WorkflowValidationFinding>> RunAsync(
        WorkflowValidationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var findings = new List<WorkflowValidationFinding>();
        var rolesByAgent = new Dictionary<string, IReadOnlyList<AgentRole>>(StringComparer.Ordinal);
        var seenAgents = new HashSet<string>(StringComparer.Ordinal);

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

            // Each agent fires at most one finding per validation run, even if referenced by
            // multiple nodes. The first node that surfaces it owns the location anchor.
            if (!seenAgents.Add(agentKey))
            {
                continue;
            }

            if (!rolesByAgent.TryGetValue(agentKey, out var roles))
            {
                roles = await context.AgentRoleRepository
                    .GetRolesForAgentAsync(agentKey, cancellationToken);
                rolesByAgent[agentKey] = roles;
            }

            if (roles.Count > 0)
            {
                continue;
            }

            // Zero roles. Decide error vs warning by scanning the resolved agent's prompts for
            // a host-tool or MCP capability mention.
            var capability = await TryDetectCapabilityAsync(context, node, agentKey, cancellationToken);

            if (capability is not null)
            {
                findings.Add(new WorkflowValidationFinding(
                    RuleId: RuleId,
                    Severity: WorkflowValidationSeverity.Error,
                    Message: $"Agent '{agentKey}' has no role assignments but its prompt mentions "
                        + $"the '{capability}' capability. The agent will have no host tools or "
                        + "MCP grants at runtime and the call will fail. Assign a role on the "
                        + "Roles page that grants this capability.",
                    Location: new WorkflowValidationLocation(NodeId: node.Id)));
            }
            else
            {
                findings.Add(new WorkflowValidationFinding(
                    RuleId: RuleId,
                    Severity: WorkflowValidationSeverity.Warning,
                    Message: $"Agent '{agentKey}' has no role assignments. Pure-text agents "
                        + "(classifiers, PRD producers) legitimately need no tools, so this is "
                        + "informational — but if the agent is meant to read files, apply patches, "
                        + "run commands, or call MCP tools, assign a role on the Roles page.",
                    Location: new WorkflowValidationLocation(NodeId: node.Id)));
            }
        }

        return findings;
    }

    private static async Task<string?> TryDetectCapabilityAsync(
        WorkflowValidationContext context,
        WorkflowNodeDto node,
        string agentKey,
        CancellationToken cancellationToken)
    {
        int version;
        try
        {
            version = node.AgentVersion ?? await context.AgentRepository
                .GetLatestVersionAsync(agentKey, cancellationToken);
        }
        catch (AgentConfigNotFoundException)
        {
            return null;
        }

        AgentConfig agentConfig;
        try
        {
            agentConfig = await context.AgentRepository.GetAsync(
                agentKey, version, cancellationToken);
        }
        catch (AgentConfigNotFoundException)
        {
            return null;
        }

        var systemPrompt = agentConfig.Configuration?.SystemPrompt;
        var promptTemplate = agentConfig.Configuration?.PromptTemplate;

        return FindCapability(systemPrompt) ?? FindCapability(promptTemplate);
    }

    private static string? FindCapability(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        var match = CapabilityRegex.Match(prompt);
        return match.Success ? match.Value : null;
    }

    private static bool IsAgentPinnedNodeKind(WorkflowNodeKind kind) =>
        kind is WorkflowNodeKind.Start or WorkflowNodeKind.Agent or WorkflowNodeKind.Hitl;
}
