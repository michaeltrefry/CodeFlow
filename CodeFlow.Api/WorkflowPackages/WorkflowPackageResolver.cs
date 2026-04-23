using CodeFlow.Persistence;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Api.WorkflowPackages;

public sealed class WorkflowPackageResolver(
    IWorkflowRepository workflowRepository,
    IAgentConfigRepository agentConfigRepository,
    IAgentRoleRepository agentRoleRepository,
    ISkillRepository skillRepository,
    IMcpServerRepository mcpServerRepository) : IWorkflowPackageResolver
{
    public async Task<WorkflowPackage> ResolveAsync(
        string workflowKey,
        int workflowVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowKey);

        var normalizedWorkflowKey = workflowKey.Trim();
        var state = new ResolutionState();

        await AddWorkflowAsync(normalizedWorkflowKey, workflowVersion, state, cancellationToken);

        return new WorkflowPackage(
            SchemaVersion: WorkflowPackageDefaults.SchemaVersion,
            Metadata: new WorkflowPackageMetadata(
                ExportedFrom: "CodeFlow",
                ExportedAtUtc: DateTime.UtcNow),
            EntryPoint: new WorkflowPackageReference(normalizedWorkflowKey, workflowVersion),
            Workflows: state.Workflows.Values
                .OrderBy(workflow => workflow.Key, StringComparer.Ordinal)
                .ThenBy(workflow => workflow.Version)
                .ToArray(),
            Agents: state.Agents.Values
                .OrderBy(agent => agent.Key, StringComparer.Ordinal)
                .ThenBy(agent => agent.Version)
                .ToArray(),
            AgentRoleAssignments: state.AgentRoleAssignments.Values
                .OrderBy(assignment => assignment.AgentKey, StringComparer.Ordinal)
                .ToArray(),
            Roles: state.Roles.Values
                .OrderBy(role => role.Key, StringComparer.Ordinal)
                .ToArray(),
            Skills: state.Skills.Values
                .OrderBy(skill => skill.Name, StringComparer.Ordinal)
                .ToArray(),
            McpServers: state.McpServers.Values
                .OrderBy(server => server.Key, StringComparer.Ordinal)
                .ToArray());
    }

    private async Task AddWorkflowAsync(
        string workflowKey,
        int workflowVersion,
        ResolutionState state,
        CancellationToken cancellationToken)
    {
        var workflowIdentity = new WorkflowIdentity(workflowKey, workflowVersion);
        if (!state.VisitedWorkflows.Add(workflowIdentity))
        {
            return;
        }

        var workflow = await workflowRepository.GetAsync(workflowKey, workflowVersion, cancellationToken);
        var nodes = new List<WorkflowPackageWorkflowNode>(workflow.Nodes.Count);

        foreach (var node in workflow.Nodes)
        {
            var resolvedAgentVersion = await ResolveAgentVersionAsync(node, state, cancellationToken);
            if (!string.IsNullOrWhiteSpace(node.AgentKey) && resolvedAgentVersion is int concreteAgentVersion)
            {
                await AddAgentAsync(node.AgentKey!, concreteAgentVersion, state, cancellationToken);
            }

            var resolvedSubflowVersion = await ResolveSubflowVersionAsync(node, state, cancellationToken);
            if (!string.IsNullOrWhiteSpace(node.SubflowKey) && resolvedSubflowVersion is int concreteSubflowVersion)
            {
                await AddWorkflowAsync(node.SubflowKey!, concreteSubflowVersion, state, cancellationToken);
            }

            nodes.Add(new WorkflowPackageWorkflowNode(
                Id: node.Id,
                Kind: node.Kind,
                AgentKey: NormalizeOptional(node.AgentKey),
                AgentVersion: resolvedAgentVersion,
                Script: node.Script,
                OutputPorts: node.OutputPorts.ToArray(),
                LayoutX: node.LayoutX,
                LayoutY: node.LayoutY,
                SubflowKey: NormalizeOptional(node.SubflowKey),
                SubflowVersion: resolvedSubflowVersion));
        }

        state.Workflows[workflowIdentity] = new WorkflowPackageWorkflow(
            Key: workflow.Key,
            Version: workflow.Version,
            Name: workflow.Name,
            MaxRoundsPerRound: workflow.MaxRoundsPerRound,
            CreatedAtUtc: workflow.CreatedAtUtc,
            Nodes: nodes,
            Edges: workflow.Edges
                .Select(edge => new WorkflowPackageWorkflowEdge(
                    FromNodeId: edge.FromNodeId,
                    FromPort: edge.FromPort,
                    ToNodeId: edge.ToNodeId,
                    ToPort: edge.ToPort,
                    RotatesRound: edge.RotatesRound,
                    SortOrder: edge.SortOrder))
                .ToArray(),
            Inputs: workflow.Inputs
                .Select(input => new WorkflowPackageWorkflowInput(
                    Key: input.Key,
                    DisplayName: input.DisplayName,
                    Kind: input.Kind,
                    Required: input.Required,
                    DefaultValueJson: input.DefaultValueJson,
                    Description: input.Description,
                    Ordinal: input.Ordinal))
                .ToArray());
    }

    private async Task<int?> ResolveAgentVersionAsync(
        WorkflowNode node,
        ResolutionState state,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(node.AgentKey))
        {
            return null;
        }

        if (node.AgentVersion is int pinnedAgentVersion)
        {
            return pinnedAgentVersion;
        }

        if (state.LatestAgentVersions.TryGetValue(node.AgentKey, out var cachedAgentVersion))
        {
            return cachedAgentVersion;
        }

        var latestAgentVersion = await agentConfigRepository.GetLatestVersionAsync(node.AgentKey, cancellationToken);
        state.LatestAgentVersions[node.AgentKey] = latestAgentVersion;
        return latestAgentVersion;
    }

    private async Task<int?> ResolveSubflowVersionAsync(
        WorkflowNode node,
        ResolutionState state,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(node.SubflowKey))
        {
            return null;
        }

        if (node.SubflowVersion is int pinnedSubflowVersion)
        {
            return pinnedSubflowVersion;
        }

        if (state.LatestWorkflowVersions.TryGetValue(node.SubflowKey, out var cachedWorkflowVersion))
        {
            return cachedWorkflowVersion;
        }

        var latestWorkflow = await workflowRepository.GetLatestAsync(node.SubflowKey, cancellationToken)
            ?? throw new WorkflowPackageResolutionException(
                $"Workflow package export could not resolve latest version for subflow '{node.SubflowKey}'.");

        state.LatestWorkflowVersions[node.SubflowKey] = latestWorkflow.Version;
        return latestWorkflow.Version;
    }

    private async Task AddAgentAsync(
        string agentKey,
        int agentVersion,
        ResolutionState state,
        CancellationToken cancellationToken)
    {
        var normalizedAgentKey = agentKey.Trim();
        var agentIdentity = new AgentIdentity(normalizedAgentKey, agentVersion);
        if (state.VisitedAgents.Add(agentIdentity))
        {
            var agent = await agentConfigRepository.GetAsync(normalizedAgentKey, agentVersion, cancellationToken);
            state.Agents[agentIdentity] = new WorkflowPackageAgent(
                Key: agent.Key,
                Version: agent.Version,
                Kind: agent.Kind,
                Config: ParseJsonNode(agent.ConfigJson, $"agent '{agent.Key}' v{agent.Version} config"),
                CreatedAtUtc: agent.CreatedAtUtc,
                CreatedBy: agent.CreatedBy,
                Outputs: agent.DeclaredOutputs
                    .Select(output => new WorkflowPackageAgentOutput(
                        Kind: output.Kind,
                        Description: output.Description,
                        PayloadExample: output.PayloadExample is JsonElement payload
                            ? JsonNode.Parse(payload.GetRawText())
                            : null))
                    .ToArray());
        }

        var roles = await agentRoleRepository.GetRolesForAgentAsync(normalizedAgentKey, cancellationToken);
        var roleKeys = roles
            .Select(role => role.Key)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(roleKey => roleKey, StringComparer.Ordinal)
            .ToArray();

        state.AgentRoleAssignments[normalizedAgentKey] = new WorkflowPackageAgentRoleAssignment(
            AgentKey: normalizedAgentKey,
            RoleKeys: roleKeys);

        foreach (var role in roles)
        {
            await AddRoleAsync(role, state, cancellationToken);
        }
    }

    private async Task AddRoleAsync(
        AgentRole role,
        ResolutionState state,
        CancellationToken cancellationToken)
    {
        if (state.Roles.ContainsKey(role.Key))
        {
            return;
        }

        var grants = await agentRoleRepository.GetGrantsAsync(role.Id, cancellationToken);
        var skillIds = await agentRoleRepository.GetSkillGrantsAsync(role.Id, cancellationToken);
        var skillNames = new List<string>(skillIds.Count);

        foreach (var skillId in skillIds.Distinct())
        {
            var skill = await skillRepository.GetAsync(skillId, cancellationToken)
                ?? throw new WorkflowPackageResolutionException(
                    $"Workflow package export could not resolve skill id '{skillId}' for role '{role.Key}'.");

            skillNames.Add(skill.Name);
            state.Skills[skill.Name] = new WorkflowPackageSkill(
                Name: skill.Name,
                Body: skill.Body,
                IsArchived: skill.IsArchived,
                CreatedAtUtc: skill.CreatedAtUtc,
                CreatedBy: skill.CreatedBy,
                UpdatedAtUtc: skill.UpdatedAtUtc,
                UpdatedBy: skill.UpdatedBy);
        }

        foreach (var mcpServerKey in grants
                     .Where(grant => grant.Category == AgentRoleToolCategory.Mcp)
                     .Select(grant => ParseMcpServerKey(grant.ToolIdentifier))
                     .Distinct(StringComparer.Ordinal))
        {
            await AddMcpServerAsync(mcpServerKey, state, cancellationToken);
        }

        state.Roles[role.Key] = new WorkflowPackageRole(
            Key: role.Key,
            DisplayName: role.DisplayName,
            Description: role.Description,
            IsArchived: role.IsArchived,
            ToolGrants: grants
                .OrderBy(grant => grant.Category)
                .ThenBy(grant => grant.ToolIdentifier, StringComparer.Ordinal)
                .Select(grant => new WorkflowPackageRoleGrant(grant.Category, grant.ToolIdentifier))
                .ToArray(),
            SkillNames: skillNames
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
    }

    private async Task AddMcpServerAsync(
        string serverKey,
        ResolutionState state,
        CancellationToken cancellationToken)
    {
        if (state.McpServers.ContainsKey(serverKey))
        {
            return;
        }

        var server = await mcpServerRepository.GetByKeyAsync(serverKey, cancellationToken)
            ?? throw new WorkflowPackageResolutionException(
                $"Workflow package export could not resolve MCP server '{serverKey}'.");

        var tools = await mcpServerRepository.GetToolsAsync(server.Id, cancellationToken);
        state.McpServers[server.Key] = new WorkflowPackageMcpServer(
            Key: server.Key,
            DisplayName: server.DisplayName,
            Transport: server.Transport,
            EndpointUrl: server.EndpointUrl,
            HasBearerToken: server.HasBearerToken,
            HealthStatus: server.HealthStatus,
            LastVerifiedAtUtc: server.LastVerifiedAtUtc,
            LastVerificationError: server.LastVerificationError,
            IsArchived: server.IsArchived,
            Tools: tools
                .OrderBy(tool => tool.ToolName, StringComparer.Ordinal)
                .Select(tool => new WorkflowPackageMcpServerTool(
                    ToolName: tool.ToolName,
                    Description: tool.Description,
                    Parameters: string.IsNullOrWhiteSpace(tool.ParametersJson)
                        ? null
                        : ParseJsonNode(tool.ParametersJson, $"MCP server '{server.Key}' tool '{tool.ToolName}' parameters"),
                    IsMutating: tool.IsMutating,
                    SyncedAtUtc: tool.SyncedAtUtc))
                .ToArray());
    }

    private static string ParseMcpServerKey(string toolIdentifier)
    {
        var parts = toolIdentifier.Split(':', 3, StringSplitOptions.None);
        if (parts.Length != 3 || !string.Equals(parts[0], "mcp", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowPackageResolutionException(
                $"Workflow package export expected MCP tool identifier to use the form 'mcp:<server_key>:<tool_name>', but got '{toolIdentifier}'.");
        }

        return parts[1].Trim();
    }

    private static JsonNode ParseJsonNode(string json, string context)
    {
        try
        {
            return JsonNode.Parse(json) ?? throw new WorkflowPackageResolutionException(
                $"Workflow package export could not parse {context} because the JSON value was null.");
        }
        catch (JsonException exception)
        {
            throw new WorkflowPackageResolutionException(
                $"Workflow package export could not parse {context} as valid JSON.",
                exception);
        }
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class ResolutionState
    {
        public HashSet<WorkflowIdentity> VisitedWorkflows { get; } = new();

        public HashSet<AgentIdentity> VisitedAgents { get; } = new();

        public Dictionary<string, int> LatestWorkflowVersions { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> LatestAgentVersions { get; } = new(StringComparer.Ordinal);

        public Dictionary<WorkflowIdentity, WorkflowPackageWorkflow> Workflows { get; } = new();

        public Dictionary<AgentIdentity, WorkflowPackageAgent> Agents { get; } = new();

        public Dictionary<string, WorkflowPackageAgentRoleAssignment> AgentRoleAssignments { get; } =
            new(StringComparer.Ordinal);

        public Dictionary<string, WorkflowPackageRole> Roles { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, WorkflowPackageSkill> Skills { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, WorkflowPackageMcpServer> McpServers { get; } = new(StringComparer.Ordinal);
    }

    private readonly record struct WorkflowIdentity(string Key, int Version);

    private readonly record struct AgentIdentity(string Key, int Version);
}
