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

        await AddWorkflowAsync(
            normalizedWorkflowKey,
            workflowVersion,
            referencedBy: "package entry point",
            state,
            cancellationToken);

        if (state.MissingReferences.Count > 0)
        {
            // V8: fail with the full list, not just the first missing reference. Authors fixing
            // the package can address everything in one pass instead of N round-trips.
            throw new WorkflowPackageResolutionException(
                "Workflow package export failed self-containment check. "
                    + $"Missing {state.MissingReferences.Count} reference(s): "
                    + string.Join("; ", state.MissingReferences.Select(FormatMissing)),
                state.MissingReferences);
        }

        var workflows = state.Workflows.Values
            .OrderBy(workflow => workflow.Key, StringComparer.Ordinal)
            .ThenBy(workflow => workflow.Version)
            .ToArray();
        var agents = state.Agents.Values
            .OrderBy(agent => agent.Key, StringComparer.Ordinal)
            .ThenBy(agent => agent.Version)
            .ToArray();
        var roles = state.Roles.Values
            .OrderBy(role => role.Key, StringComparer.Ordinal)
            .ToArray();
        var skills = state.Skills.Values
            .OrderBy(skill => skill.Name, StringComparer.Ordinal)
            .ToArray();
        var mcpServers = state.McpServers.Values
            .OrderBy(server => server.Key, StringComparer.Ordinal)
            .ToArray();

        return new WorkflowPackage(
            SchemaVersion: WorkflowPackageDefaults.SchemaVersion,
            Metadata: new WorkflowPackageMetadata(
                ExportedFrom: "CodeFlow",
                ExportedAtUtc: DateTime.UtcNow),
            EntryPoint: new WorkflowPackageReference(normalizedWorkflowKey, workflowVersion),
            Workflows: workflows,
            Agents: agents,
            AgentRoleAssignments: state.AgentRoleAssignments.Values
                .OrderBy(assignment => assignment.AgentKey, StringComparer.Ordinal)
                .ToArray(),
            Roles: roles,
            Skills: skills,
            McpServers: mcpServers,
            Manifest: new WorkflowPackageManifest(
                Workflows: workflows
                    .Select(workflow => new WorkflowPackageReference(workflow.Key, workflow.Version))
                    .ToArray(),
                Agents: agents
                    .Select(agent => new WorkflowPackageReference(agent.Key, agent.Version))
                    .ToArray(),
                Roles: roles.Select(role => role.Key).ToArray(),
                Skills: skills.Select(skill => skill.Name).ToArray(),
                McpServers: mcpServers.Select(server => server.Key).ToArray()));
    }

    private static string FormatMissing(MissingPackageReference reference)
    {
        var version = reference.Version is int v ? $" v{v}" : string.Empty;
        return $"{reference.Kind} '{reference.Key}'{version} (referenced by {reference.ReferencedBy})";
    }

    private async Task AddWorkflowAsync(
        string workflowKey,
        int workflowVersion,
        string referencedBy,
        ResolutionState state,
        CancellationToken cancellationToken)
    {
        var workflowIdentity = new WorkflowIdentity(workflowKey, workflowVersion);
        if (!state.VisitedWorkflows.Add(workflowIdentity))
        {
            return;
        }

        Workflow workflow;
        try
        {
            workflow = await workflowRepository.GetAsync(workflowKey, workflowVersion, cancellationToken);
        }
        catch (WorkflowNotFoundException)
        {
            state.MissingReferences.Add(new MissingPackageReference(
                PackageReferenceKind.Workflow, workflowKey, workflowVersion, referencedBy));
            return;
        }
        var nodes = new List<WorkflowPackageWorkflowNode>(workflow.Nodes.Count);

        foreach (var node in workflow.Nodes)
        {
            var nodeOrigin = $"workflow '{workflow.Key}' v{workflow.Version} node {node.Id}";
            var resolvedAgentVersion = await ResolveAgentVersionAsync(node, nodeOrigin, state, cancellationToken);
            if (!string.IsNullOrWhiteSpace(node.AgentKey) && resolvedAgentVersion is int concreteAgentVersion)
            {
                await AddAgentAsync(node.AgentKey!, concreteAgentVersion, nodeOrigin, state, cancellationToken);
            }

            var resolvedSubflowVersion = await ResolveSubflowVersionAsync(node, nodeOrigin, state, cancellationToken);
            if (!string.IsNullOrWhiteSpace(node.SubflowKey) && resolvedSubflowVersion is int concreteSubflowVersion)
            {
                await AddWorkflowAsync(node.SubflowKey!, concreteSubflowVersion, nodeOrigin, state, cancellationToken);
            }

            // Swarm-node agents (sc-43 / sc-46): the contributor, synthesizer, and (Coordinator-only)
            // coordinator agents are referenced via dedicated fields, not the generic AgentKey. Pull
            // each into the package so the export is self-contained.
            if (node.Kind == WorkflowNodeKind.Swarm)
            {
                if (!string.IsNullOrWhiteSpace(node.ContributorAgentKey)
                    && node.ContributorAgentVersion is int contributorVersion)
                {
                    await AddAgentAsync(node.ContributorAgentKey!, contributorVersion, nodeOrigin, state, cancellationToken);
                }
                if (!string.IsNullOrWhiteSpace(node.SynthesizerAgentKey)
                    && node.SynthesizerAgentVersion is int synthesizerVersion)
                {
                    await AddAgentAsync(node.SynthesizerAgentKey!, synthesizerVersion, nodeOrigin, state, cancellationToken);
                }
                if (!string.IsNullOrWhiteSpace(node.CoordinatorAgentKey)
                    && node.CoordinatorAgentVersion is int coordinatorVersion)
                {
                    await AddAgentAsync(node.CoordinatorAgentKey!, coordinatorVersion, nodeOrigin, state, cancellationToken);
                }
            }

            nodes.Add(new WorkflowPackageWorkflowNode(
                Id: node.Id,
                Kind: node.Kind,
                AgentKey: NormalizeOptional(node.AgentKey),
                AgentVersion: resolvedAgentVersion,
                OutputScript: node.OutputScript,
                OutputPorts: node.OutputPorts.ToArray(),
                LayoutX: node.LayoutX,
                LayoutY: node.LayoutY,
                SubflowKey: NormalizeOptional(node.SubflowKey),
                SubflowVersion: resolvedSubflowVersion,
                ReviewMaxRounds: node.ReviewMaxRounds,
                LoopDecision: NormalizeOptional(node.LoopDecision),
                InputScript: node.InputScript,
                OptOutLastRoundReminder: node.OptOutLastRoundReminder,
                RejectionHistory: node.RejectionHistory,
                MirrorOutputToWorkflowVar: NormalizeOptional(node.MirrorOutputToWorkflowVar),
                OutputPortReplacements: node.OutputPortReplacements,
                Template: node.Template,
                OutputType: node.OutputType,
                SwarmProtocol: NormalizeOptional(node.SwarmProtocol),
                SwarmN: node.SwarmN,
                ContributorAgentKey: NormalizeOptional(node.ContributorAgentKey),
                ContributorAgentVersion: node.ContributorAgentVersion,
                SynthesizerAgentKey: NormalizeOptional(node.SynthesizerAgentKey),
                SynthesizerAgentVersion: node.SynthesizerAgentVersion,
                CoordinatorAgentKey: NormalizeOptional(node.CoordinatorAgentKey),
                CoordinatorAgentVersion: node.CoordinatorAgentVersion,
                SwarmTokenBudget: node.SwarmTokenBudget));
        }

        state.Workflows[workflowIdentity] = new WorkflowPackageWorkflow(
            Key: workflow.Key,
            Version: workflow.Version,
            Name: workflow.Name,
            MaxRoundsPerRound: workflow.MaxRoundsPerRound,
            Category: workflow.Category,
            Tags: workflow.TagsOrEmpty.ToArray(),
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
                .ToArray(),
            WorkflowVarsReads: workflow.WorkflowVarsReads,
            WorkflowVarsWrites: workflow.WorkflowVarsWrites);
    }

    private async Task<int?> ResolveAgentVersionAsync(
        WorkflowNode node,
        string referencedBy,
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

        try
        {
            var latestAgentVersion = await agentConfigRepository.GetLatestVersionAsync(
                node.AgentKey, cancellationToken);
            state.LatestAgentVersions[node.AgentKey] = latestAgentVersion;
            return latestAgentVersion;
        }
        catch (AgentConfigNotFoundException)
        {
            state.MissingReferences.Add(new MissingPackageReference(
                PackageReferenceKind.Agent, node.AgentKey, Version: null, referencedBy));
            return null;
        }
    }

    private async Task<int?> ResolveSubflowVersionAsync(
        WorkflowNode node,
        string referencedBy,
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

        var latestWorkflow = await workflowRepository.GetLatestAsync(node.SubflowKey, cancellationToken);
        if (latestWorkflow is null)
        {
            state.MissingReferences.Add(new MissingPackageReference(
                PackageReferenceKind.Workflow, node.SubflowKey, Version: null, referencedBy));
            return null;
        }

        state.LatestWorkflowVersions[node.SubflowKey] = latestWorkflow.Version;
        return latestWorkflow.Version;
    }

    private async Task AddAgentAsync(
        string agentKey,
        int agentVersion,
        string referencedBy,
        ResolutionState state,
        CancellationToken cancellationToken)
    {
        var normalizedAgentKey = agentKey.Trim();
        var agentIdentity = new AgentIdentity(normalizedAgentKey, agentVersion);
        if (state.VisitedAgents.Add(agentIdentity))
        {
            AgentConfig agent;
            try
            {
                agent = await agentConfigRepository.GetAsync(normalizedAgentKey, agentVersion, cancellationToken);
            }
            catch (AgentConfigNotFoundException)
            {
                state.MissingReferences.Add(new MissingPackageReference(
                    PackageReferenceKind.Agent, normalizedAgentKey, agentVersion, referencedBy));
                return;
            }
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
            await AddRoleAsync(role, $"agent '{normalizedAgentKey}'", state, cancellationToken);
        }
    }

    private async Task AddRoleAsync(
        AgentRole role,
        string referencedBy,
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
            var skill = await skillRepository.GetAsync(skillId, cancellationToken);
            if (skill is null)
            {
                state.MissingReferences.Add(new MissingPackageReference(
                    PackageReferenceKind.Skill,
                    Key: skillId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Version: null,
                    ReferencedBy: $"role '{role.Key}'"));
                continue;
            }

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
            await AddMcpServerAsync(mcpServerKey, $"role '{role.Key}'", state, cancellationToken);
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
        string referencedBy,
        ResolutionState state,
        CancellationToken cancellationToken)
    {
        if (state.McpServers.ContainsKey(serverKey))
        {
            return;
        }

        var server = await mcpServerRepository.GetByKeyAsync(serverKey, cancellationToken);
        if (server is null)
        {
            state.MissingReferences.Add(new MissingPackageReference(
                PackageReferenceKind.McpServer, serverKey, Version: null, referencedBy));
            return;
        }

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

        public List<MissingPackageReference> MissingReferences { get; } = new();
    }

    private readonly record struct WorkflowIdentity(string Key, int Version);

    private readonly record struct AgentIdentity(string Key, int Version);
}
