using CodeFlow.Persistence;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Api.WorkflowPackages;

/// <summary>
/// Builds an <see cref="AgentPackage"/> from a single (agent key, version) by transitively
/// loading the role / skill / MCP-server closure. Mirrors the dependency-closure half of
/// <see cref="WorkflowPackageResolver"/>; the two are kept separate because their entry-point
/// handling and missing-reference messaging diverge.
/// </summary>
public sealed class AgentPackageResolver(
    IAgentConfigRepository agentConfigRepository,
    IAgentRoleRepository agentRoleRepository,
    ISkillRepository skillRepository,
    IMcpServerRepository mcpServerRepository) : IAgentPackageResolver
{
    public async Task<AgentPackage> ResolveAsync(
        string agentKey,
        int agentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentKey);

        var normalizedKey = agentKey.Trim();
        var entryPoint = new WorkflowPackageReference(normalizedKey, agentVersion);
        var state = new ResolutionState();

        // Load the entry-point agent. A missing entry point is a 404, not a
        // self-containment failure, so let AgentConfigNotFoundException propagate;
        // missing role / skill / MCP dependencies still accumulate as 422 below.
        var agent = await agentConfigRepository.GetAsync(normalizedKey, agentVersion, cancellationToken);
        state.Agents[new AgentIdentity(normalizedKey, agentVersion)] = MapAgent(agent);

        await AddRoleAssignmentClosureAsync(normalizedKey, state, cancellationToken);

        if (state.MissingReferences.Count > 0)
        {
            throw new WorkflowPackageResolutionException(
                "Agent package export failed self-containment check. "
                    + $"Missing {state.MissingReferences.Count} reference(s): "
                    + string.Join("; ", state.MissingReferences.Select(FormatMissing)),
                state.MissingReferences);
        }

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

        return new AgentPackage(
            SchemaVersion: AgentPackageDefaults.SchemaVersion,
            Metadata: new WorkflowPackageMetadata(
                ExportedFrom: "CodeFlow",
                ExportedAtUtc: DateTime.UtcNow),
            EntryPoint: entryPoint,
            Agents: agents,
            AgentRoleAssignments: state.AgentRoleAssignments.Values
                .OrderBy(assignment => assignment.AgentKey, StringComparer.Ordinal)
                .ToArray(),
            Roles: roles,
            Skills: skills,
            McpServers: mcpServers,
            Manifest: new AgentPackageManifest(
                Agent: entryPoint,
                Roles: roles.Select(role => role.Key).ToArray(),
                Skills: skills.Select(skill => skill.Name).ToArray(),
                McpServers: mcpServers.Select(server => server.Key).ToArray()));
    }

    private static string FormatMissing(MissingPackageReference reference)
    {
        var version = reference.Version is int v ? $" v{v}" : string.Empty;
        return $"{reference.Kind} '{reference.Key}'{version} (referenced by {reference.ReferencedBy})";
    }

    private static WorkflowPackageAgent MapAgent(AgentConfig agent)
    {
        return new WorkflowPackageAgent(
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
                .ToArray(),
            Tags: agent.TagsOrEmpty.ToArray());
    }

    private async Task AddRoleAssignmentClosureAsync(
        string agentKey,
        ResolutionState state,
        CancellationToken cancellationToken)
    {
        var roles = await agentRoleRepository.GetRolesForAgentAsync(agentKey, cancellationToken);
        var roleKeys = roles
            .Select(role => role.Key)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(roleKey => roleKey, StringComparer.Ordinal)
            .ToArray();

        state.AgentRoleAssignments[agentKey] = new WorkflowPackageAgentRoleAssignment(
            AgentKey: agentKey,
            RoleKeys: roleKeys);

        foreach (var role in roles)
        {
            await AddRoleAsync(role, $"agent '{agentKey}'", state, cancellationToken);
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
                .ToArray(),
            Tags: role.TagsOrEmpty.ToArray());
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
                $"Agent package export expected MCP tool identifier to use the form 'mcp:<server_key>:<tool_name>', but got '{toolIdentifier}'.");
        }

        return parts[1].Trim();
    }

    private static JsonNode ParseJsonNode(string json, string context)
    {
        try
        {
            return JsonNode.Parse(json) ?? throw new WorkflowPackageResolutionException(
                $"Agent package export could not parse {context} because the JSON value was null.");
        }
        catch (JsonException exception)
        {
            throw new WorkflowPackageResolutionException(
                $"Agent package export could not parse {context} as valid JSON.",
                exception);
        }
    }

    private sealed class ResolutionState
    {
        public Dictionary<AgentIdentity, WorkflowPackageAgent> Agents { get; } = new();

        public Dictionary<string, WorkflowPackageAgentRoleAssignment> AgentRoleAssignments { get; } =
            new(StringComparer.Ordinal);

        public Dictionary<string, WorkflowPackageRole> Roles { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, WorkflowPackageSkill> Skills { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, WorkflowPackageMcpServer> McpServers { get; } = new(StringComparer.Ordinal);

        public List<MissingPackageReference> MissingReferences { get; } = new();
    }

    private readonly record struct AgentIdentity(string Key, int Version);
}
