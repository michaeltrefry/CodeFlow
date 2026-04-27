using CodeFlow.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Api.WorkflowPackages;

public sealed class WorkflowPackageImporter(
    CodeFlowDbContext dbContext,
    IWorkflowRepository workflowRepository,
    IAgentConfigRepository agentConfigRepository,
    IAgentRoleRepository agentRoleRepository,
    ISkillRepository skillRepository,
    IMcpServerRepository mcpServerRepository) : IWorkflowPackageImporter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<WorkflowPackageImportPreview> PreviewAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ValidatePackage(package);

        var items = new List<WorkflowPackageImportItem>();
        var warnings = new List<string>();

        foreach (var workflow in package.Workflows)
        {
            items.Add(await PreviewWorkflowAsync(workflow, cancellationToken));
        }

        foreach (var agent in package.Agents)
        {
            items.Add(await PreviewAgentAsync(agent, cancellationToken));
        }

        foreach (var assignment in package.AgentRoleAssignments)
        {
            items.Add(await PreviewAgentRoleAssignmentAsync(assignment, cancellationToken));
        }

        foreach (var role in package.Roles)
        {
            items.Add(await PreviewRoleAsync(role, cancellationToken));
        }

        foreach (var skill in package.Skills)
        {
            items.Add(await PreviewSkillAsync(skill, cancellationToken));
        }

        foreach (var server in package.McpServers)
        {
            items.Add(await PreviewMcpServerAsync(server, warnings, cancellationToken));
        }

        return new WorkflowPackageImportPreview(
            package.EntryPoint,
            items
                .OrderBy(item => item.Kind)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .ThenBy(item => item.Version)
                .ToArray(),
            warnings.ToArray());
    }

    public async Task<WorkflowPackageImportApplyResult> ApplyAsync(
        WorkflowPackage package,
        CancellationToken cancellationToken = default)
    {
        var preview = await PreviewAsync(package, cancellationToken);
        if (!preview.CanApply)
        {
            throw new WorkflowPackageResolutionException(
                "Workflow package import has conflicts. Preview the package and resolve conflicts before applying it.");
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            var now = DateTime.UtcNow;

            await ImportSkillsAsync(package, now, cancellationToken);
            await ImportMcpServersAsync(package, now, cancellationToken);
            await ImportRolesAsync(package, now, cancellationToken);
            await ImportAgentsAsync(package, now, cancellationToken);
            await ImportRoleAssignmentsAsync(package, now, cancellationToken);
            await ImportWorkflowsAsync(package, now, cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return new WorkflowPackageImportApplyResult(preview.EntryPoint, preview.Items, preview.Warnings);
    }

    private static void ValidatePackage(WorkflowPackage package)
    {
        if (!string.Equals(package.SchemaVersion, WorkflowPackageDefaults.SchemaVersion, StringComparison.Ordinal))
        {
            throw new WorkflowPackageResolutionException(
                $"Workflow package schema '{package.SchemaVersion}' is not supported.");
        }

        if (!package.Workflows.Any(workflow =>
                string.Equals(workflow.Key, package.EntryPoint.Key, StringComparison.Ordinal) &&
                workflow.Version == package.EntryPoint.Version))
        {
            throw new WorkflowPackageResolutionException(
                $"Workflow package entry point '{package.EntryPoint.Key}' v{package.EntryPoint.Version} is missing from workflows.");
        }

        var workflowKeys = package.Workflows
            .Select(workflow => new PackageVersionKey(workflow.Key, workflow.Version))
            .ToHashSet();
        var agentKeys = package.Agents
            .Select(agent => new PackageVersionKey(agent.Key, agent.Version))
            .ToHashSet();

        foreach (var workflow in package.Workflows)
        {
            foreach (var node in workflow.Nodes)
            {
                if (!string.IsNullOrWhiteSpace(node.AgentKey))
                {
                    if (node.AgentVersion is null)
                    {
                        throw new WorkflowPackageResolutionException(
                            $"Workflow '{workflow.Key}' v{workflow.Version} has agent node '{node.Id}' without a concrete agent version.");
                    }

                    if (!agentKeys.Contains(new PackageVersionKey(node.AgentKey!, node.AgentVersion.Value)))
                    {
                        throw new WorkflowPackageResolutionException(
                            $"Workflow '{workflow.Key}' v{workflow.Version} references missing agent '{node.AgentKey}' v{node.AgentVersion}.");
                    }
                }

                if (!string.IsNullOrWhiteSpace(node.SubflowKey))
                {
                    if (node.SubflowVersion is null)
                    {
                        throw new WorkflowPackageResolutionException(
                            $"Workflow '{workflow.Key}' v{workflow.Version} has subflow node '{node.Id}' without a concrete subflow version.");
                    }

                    if (!workflowKeys.Contains(new PackageVersionKey(node.SubflowKey!, node.SubflowVersion.Value)))
                    {
                        throw new WorkflowPackageResolutionException(
                            $"Workflow '{workflow.Key}' v{workflow.Version} references missing subflow '{node.SubflowKey}' v{node.SubflowVersion}.");
                    }
                }
            }
        }
    }

    private async Task<WorkflowPackageImportItem> PreviewWorkflowAsync(
        WorkflowPackageWorkflow packageWorkflow,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await workflowRepository.GetAsync(packageWorkflow.Key, packageWorkflow.Version, cancellationToken);
            return Equivalent(packageWorkflow, existing)
                ? Reuse(WorkflowPackageImportResourceKind.Workflow, packageWorkflow.Key, packageWorkflow.Version)
                : Conflict(
                    WorkflowPackageImportResourceKind.Workflow,
                    packageWorkflow.Key,
                    packageWorkflow.Version,
                    "A workflow with this key and version already exists with different graph metadata.");
        }
        catch (WorkflowNotFoundException)
        {
            return Create(WorkflowPackageImportResourceKind.Workflow, packageWorkflow.Key, packageWorkflow.Version);
        }
    }

    private async Task<WorkflowPackageImportItem> PreviewAgentAsync(
        WorkflowPackageAgent packageAgent,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await agentConfigRepository.GetAsync(packageAgent.Key, packageAgent.Version, cancellationToken);
            return Equivalent(packageAgent, existing)
                ? Reuse(WorkflowPackageImportResourceKind.Agent, packageAgent.Key, packageAgent.Version)
                : Conflict(
                    WorkflowPackageImportResourceKind.Agent,
                    packageAgent.Key,
                    packageAgent.Version,
                    "An agent with this key and version already exists with different configuration or outputs.");
        }
        catch (AgentConfigNotFoundException)
        {
            return Create(WorkflowPackageImportResourceKind.Agent, packageAgent.Key, packageAgent.Version);
        }
    }

    private async Task<WorkflowPackageImportItem> PreviewAgentRoleAssignmentAsync(
        WorkflowPackageAgentRoleAssignment assignment,
        CancellationToken cancellationToken)
    {
        var existingRoles = await agentRoleRepository.GetRolesForAgentAsync(assignment.AgentKey, cancellationToken);
        var existingRoleKeys = existingRoles
            .Select(role => role.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        var packageRoleKeys = assignment.RoleKeys
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        if (existingRoleKeys.Length == 0 && packageRoleKeys.Length > 0)
        {
            return Create(WorkflowPackageImportResourceKind.AgentRoleAssignment, assignment.AgentKey, null);
        }

        return existingRoleKeys.SequenceEqual(packageRoleKeys, StringComparer.Ordinal)
            ? Reuse(WorkflowPackageImportResourceKind.AgentRoleAssignment, assignment.AgentKey, null)
            : Conflict(
                WorkflowPackageImportResourceKind.AgentRoleAssignment,
                assignment.AgentKey,
                null,
                "This agent already has a different role assignment set.");
    }

    private async Task<WorkflowPackageImportItem> PreviewRoleAsync(
        WorkflowPackageRole packageRole,
        CancellationToken cancellationToken)
    {
        var existing = await agentRoleRepository.GetByKeyAsync(packageRole.Key, cancellationToken);
        if (existing is null)
        {
            return Create(WorkflowPackageImportResourceKind.Role, packageRole.Key, null);
        }

        var grants = await agentRoleRepository.GetGrantsAsync(existing.Id, cancellationToken);
        var skillIds = await agentRoleRepository.GetSkillGrantsAsync(existing.Id, cancellationToken);
        var skillNames = new List<string>(skillIds.Count);
        foreach (var skillId in skillIds)
        {
            var skill = await skillRepository.GetAsync(skillId, cancellationToken);
            if (skill is not null)
            {
                skillNames.Add(skill.Name);
            }
        }

        return Equivalent(packageRole, existing, grants, skillNames)
            ? Reuse(WorkflowPackageImportResourceKind.Role, packageRole.Key, null)
            : Conflict(
                WorkflowPackageImportResourceKind.Role,
                packageRole.Key,
                null,
                "A role with this key already exists with different metadata, grants, or skills.");
    }

    private async Task<WorkflowPackageImportItem> PreviewSkillAsync(
        WorkflowPackageSkill packageSkill,
        CancellationToken cancellationToken)
    {
        var existing = await skillRepository.GetByNameAsync(packageSkill.Name, cancellationToken);
        if (existing is null)
        {
            return Create(WorkflowPackageImportResourceKind.Skill, packageSkill.Name, null);
        }

        return Equivalent(packageSkill, existing)
            ? Reuse(WorkflowPackageImportResourceKind.Skill, packageSkill.Name, null)
            : Conflict(
                WorkflowPackageImportResourceKind.Skill,
                packageSkill.Name,
                null,
                "A skill with this name already exists with different body or archive state.");
    }

    private async Task<WorkflowPackageImportItem> PreviewMcpServerAsync(
        WorkflowPackageMcpServer packageServer,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (packageServer.HasBearerToken)
        {
            warnings.Add($"MCP server '{packageServer.Key}' exported with a bearer token. The token is not in the package and must be configured locally after import.");
        }

        var existing = await mcpServerRepository.GetByKeyAsync(packageServer.Key, cancellationToken);
        if (existing is null)
        {
            return Create(WorkflowPackageImportResourceKind.McpServer, packageServer.Key, null);
        }

        var tools = await mcpServerRepository.GetToolsAsync(existing.Id, cancellationToken);
        return Equivalent(packageServer, existing, tools)
            ? Reuse(WorkflowPackageImportResourceKind.McpServer, packageServer.Key, null)
            : Conflict(
                WorkflowPackageImportResourceKind.McpServer,
                packageServer.Key,
                null,
                "An MCP server with this key already exists with different metadata or tools.");
    }

    private async Task ImportSkillsAsync(
        WorkflowPackage package,
        DateTime now,
        CancellationToken cancellationToken)
    {
        foreach (var skill in package.Skills)
        {
            if (await dbContext.Skills.AnyAsync(existing => existing.Name == skill.Name, cancellationToken))
            {
                continue;
            }

            dbContext.Skills.Add(new SkillEntity
            {
                Name = skill.Name.Trim(),
                Body = skill.Body,
                CreatedAtUtc = UsePackageDateOrNow(skill.CreatedAtUtc, now),
                CreatedBy = Trim(skill.CreatedBy),
                UpdatedAtUtc = UsePackageDateOrNow(skill.UpdatedAtUtc, now),
                UpdatedBy = Trim(skill.UpdatedBy),
                IsArchived = skill.IsArchived,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ImportMcpServersAsync(
        WorkflowPackage package,
        DateTime now,
        CancellationToken cancellationToken)
    {
        foreach (var server in package.McpServers)
        {
            if (await dbContext.McpServers.AnyAsync(existing => existing.Key == server.Key, cancellationToken))
            {
                continue;
            }

            var entity = new McpServerEntity
            {
                Key = server.Key.Trim(),
                DisplayName = server.DisplayName.Trim(),
                Transport = server.Transport,
                EndpointUrl = server.EndpointUrl.Trim(),
                BearerTokenCipher = null,
                HealthStatus = server.HealthStatus,
                LastVerifiedAtUtc = server.LastVerifiedAtUtc,
                LastVerificationError = Trim(server.LastVerificationError),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                IsArchived = server.IsArchived,
            };

            dbContext.McpServers.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);

            dbContext.McpServerTools.AddRange(server.Tools
                .Select(tool => new McpServerToolEntity
                {
                    ServerId = entity.Id,
                    ToolName = tool.ToolName.Trim(),
                    Description = Trim(tool.Description),
                    ParametersJson = tool.Parameters?.ToJsonString(SerializerOptions),
                    IsMutating = tool.IsMutating,
                    SyncedAtUtc = UsePackageDateOrNow(tool.SyncedAtUtc, now),
                }));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ImportRolesAsync(
        WorkflowPackage package,
        DateTime now,
        CancellationToken cancellationToken)
    {
        foreach (var role in package.Roles)
        {
            if (await dbContext.AgentRoles.AnyAsync(existing => existing.Key == role.Key, cancellationToken))
            {
                continue;
            }

            var skillIds = await dbContext.Skills
                .Where(skill => role.SkillNames.Contains(skill.Name))
                .Select(skill => new { skill.Name, skill.Id })
                .ToDictionaryAsync(skill => skill.Name, skill => skill.Id, StringComparer.Ordinal, cancellationToken);

            var missingSkills = role.SkillNames.Where(skillName => !skillIds.ContainsKey(skillName)).ToArray();
            if (missingSkills.Length > 0)
            {
                throw new WorkflowPackageResolutionException(
                    $"Workflow package import could not resolve skill '{missingSkills[0]}' for role '{role.Key}'.");
            }

            var entity = new AgentRoleEntity
            {
                Key = role.Key.Trim(),
                DisplayName = role.DisplayName.Trim(),
                Description = Trim(role.Description),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                IsArchived = role.IsArchived,
            };

            dbContext.AgentRoles.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);

            dbContext.AgentRoleToolGrants.AddRange(role.ToolGrants
                .Select(grant => new AgentRoleToolGrantEntity
                {
                    RoleId = entity.Id,
                    Category = grant.Category,
                    ToolIdentifier = grant.ToolIdentifier.Trim(),
                }));
            dbContext.AgentRoleSkillGrants.AddRange(role.SkillNames
                .Select(skillName => new AgentRoleSkillGrantEntity
                {
                    RoleId = entity.Id,
                    SkillId = skillIds[skillName],
                }));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ImportAgentsAsync(
        WorkflowPackage package,
        DateTime now,
        CancellationToken cancellationToken)
    {
        foreach (var group in package.Agents.GroupBy(agent => agent.Key, StringComparer.Ordinal))
        {
            var packageVersions = group.OrderBy(agent => agent.Version).ToArray();
            var maxVersion = await dbContext.Agents
                .Where(agent => agent.Key == group.Key)
                .Select(agent => (int?)agent.Version)
                .MaxAsync(cancellationToken);
            var finalMaxVersion = Math.Max(maxVersion ?? 0, packageVersions.Max(agent => agent.Version));

            foreach (var existingActive in await dbContext.Agents
                         .Where(agent => agent.Key == group.Key && agent.IsActive)
                         .ToListAsync(cancellationToken))
            {
                existingActive.IsActive = existingActive.Version == finalMaxVersion;
            }

            foreach (var agent in packageVersions)
            {
                if (await dbContext.Agents.AnyAsync(
                        existing => existing.Key == agent.Key && existing.Version == agent.Version,
                        cancellationToken))
                {
                    continue;
                }

                dbContext.Agents.Add(new AgentConfigEntity
                {
                    Key = agent.Key.Trim(),
                    Version = agent.Version,
                    ConfigJson = agent.Config?.ToJsonString(SerializerOptions) ?? "{}",
                    CreatedAtUtc = UsePackageDateOrNow(agent.CreatedAtUtc, now),
                    CreatedBy = Trim(agent.CreatedBy),
                    IsActive = agent.Version == finalMaxVersion,
                    IsRetired = false,
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ImportRoleAssignmentsAsync(
        WorkflowPackage package,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var roleIds = await dbContext.AgentRoles
            .Where(role => package.Roles.Select(packageRole => packageRole.Key).Contains(role.Key))
            .Select(role => new { role.Key, role.Id })
            .ToDictionaryAsync(role => role.Key, role => role.Id, StringComparer.Ordinal, cancellationToken);

        foreach (var assignment in package.AgentRoleAssignments)
        {
            if (await dbContext.AgentRoleAssignments.AnyAsync(
                    existing => existing.AgentKey == assignment.AgentKey,
                    cancellationToken))
            {
                continue;
            }

            foreach (var roleKey in assignment.RoleKeys.Distinct(StringComparer.Ordinal))
            {
                if (!roleIds.TryGetValue(roleKey, out var roleId))
                {
                    throw new WorkflowPackageResolutionException(
                        $"Workflow package import could not resolve role '{roleKey}' for agent '{assignment.AgentKey}'.");
                }

                dbContext.AgentRoleAssignments.Add(new AgentRoleAssignmentEntity
                {
                    AgentKey = assignment.AgentKey.Trim(),
                    RoleId = roleId,
                    CreatedAtUtc = now,
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ImportWorkflowsAsync(
        WorkflowPackage package,
        DateTime now,
        CancellationToken cancellationToken)
    {
        foreach (var workflow in package.Workflows.OrderBy(workflow => workflow.Key).ThenBy(workflow => workflow.Version))
        {
            if (await dbContext.Workflows.AnyAsync(
                    existing => existing.Key == workflow.Key && existing.Version == workflow.Version,
                    cancellationToken))
            {
                continue;
            }

            dbContext.Workflows.Add(new WorkflowEntity
            {
                Key = workflow.Key.Trim(),
                Version = workflow.Version,
                Name = workflow.Name.Trim(),
                MaxRoundsPerRound = workflow.MaxRoundsPerRound,
                Category = workflow.Category,
                TagsJson = JsonSerializer.Serialize(workflow.Tags, SerializerOptions),
                CreatedAtUtc = UsePackageDateOrNow(workflow.CreatedAtUtc, now),
                Nodes = workflow.Nodes
                    .Select(node => new WorkflowNodeEntity
                    {
                        NodeId = node.Id,
                        Kind = node.Kind,
                        AgentKey = Trim(node.AgentKey),
                        AgentVersion = node.AgentVersion,
                        OutputScript = node.OutputScript,
                        InputScript = node.InputScript,
                        OutputPortsJson = JsonSerializer.Serialize(node.OutputPorts, SerializerOptions),
                        LayoutX = node.LayoutX,
                        LayoutY = node.LayoutY,
                        SubflowKey = Trim(node.SubflowKey),
                        SubflowVersion = node.SubflowVersion,
                        ReviewMaxRounds = node.ReviewMaxRounds,
                        LoopDecision = Trim(node.LoopDecision),
                        OptOutLastRoundReminder = node.OptOutLastRoundReminder,
                        RejectionHistoryConfigJson = WorkflowJson.SerializeRejectionHistoryConfig(node.RejectionHistory),
                        MirrorOutputToWorkflowVar = Trim(node.MirrorOutputToWorkflowVar),
                        OutputPortReplacementsJson = WorkflowJson.SerializePortReplacements(node.OutputPortReplacements),
                    })
                    .ToList(),
                Edges = workflow.Edges
                    .Select(edge => new WorkflowEdgeEntity
                    {
                        FromNodeId = edge.FromNodeId,
                        FromPort = edge.FromPort,
                        ToNodeId = edge.ToNodeId,
                        ToPort = string.IsNullOrWhiteSpace(edge.ToPort) ? WorkflowEdge.DefaultInputPort : edge.ToPort,
                        RotatesRound = edge.RotatesRound,
                        SortOrder = edge.SortOrder,
                    })
                    .ToList(),
                Inputs = workflow.Inputs
                    .Select(input => new WorkflowInputEntity
                    {
                        Key = input.Key.Trim(),
                        DisplayName = input.DisplayName.Trim(),
                        Kind = input.Kind,
                        Required = input.Required,
                        DefaultValueJson = input.DefaultValueJson,
                        Description = input.Description,
                        Ordinal = input.Ordinal,
                    })
                    .ToList(),
            });
        }
    }

    private static WorkflowPackageImportItem Create(WorkflowPackageImportResourceKind kind, string key, int? version) =>
        new(kind, key, version, WorkflowPackageImportAction.Create, "Will create this package resource.");

    private static WorkflowPackageImportItem Reuse(WorkflowPackageImportResourceKind kind, string key, int? version) =>
        new(kind, key, version, WorkflowPackageImportAction.Reuse, "Matching target resource already exists.");

    private static WorkflowPackageImportItem Conflict(
        WorkflowPackageImportResourceKind kind,
        string key,
        int? version,
        string message) =>
        new(kind, key, version, WorkflowPackageImportAction.Conflict, message);

    private static bool Equivalent(WorkflowPackageWorkflow packageWorkflow, Workflow existing) =>
        string.Equals(packageWorkflow.Name, existing.Name, StringComparison.Ordinal) &&
        packageWorkflow.MaxRoundsPerRound == existing.MaxRoundsPerRound &&
        packageWorkflow.Category == existing.Category &&
        packageWorkflow.Tags.SequenceEqual(existing.TagsOrEmpty, StringComparer.Ordinal) &&
        SerializedEquals(packageWorkflow.Nodes, existing.Nodes.Select(node =>
        {
            var packageNode = packageWorkflow.Nodes.SingleOrDefault(candidate => candidate.Id == node.Id);
            return new WorkflowPackageWorkflowNode(
            node.Id,
            node.Kind,
            NormalizeOptional(node.AgentKey),
            node.AgentVersion ?? packageNode?.AgentVersion,
            node.OutputScript,
            node.OutputPorts,
            node.LayoutX,
            node.LayoutY,
            NormalizeOptional(node.SubflowKey),
            node.SubflowVersion ?? packageNode?.SubflowVersion,
            node.ReviewMaxRounds,
            NormalizeOptional(node.LoopDecision),
            node.InputScript,
            node.OptOutLastRoundReminder,
            node.RejectionHistory,
            NormalizeOptional(node.MirrorOutputToWorkflowVar),
            node.OutputPortReplacements);
        })) &&
        SerializedEquals(packageWorkflow.Edges, existing.Edges.Select(edge => new WorkflowPackageWorkflowEdge(
            edge.FromNodeId,
            edge.FromPort,
            edge.ToNodeId,
            edge.ToPort,
            edge.RotatesRound,
            edge.SortOrder))) &&
        SerializedEquals(packageWorkflow.Inputs, existing.Inputs.Select(input => new WorkflowPackageWorkflowInput(
            input.Key,
            input.DisplayName,
            input.Kind,
            input.Required,
            input.DefaultValueJson,
            input.Description,
            input.Ordinal)));

    private static bool Equivalent(WorkflowPackageAgent packageAgent, AgentConfig existing) =>
        packageAgent.Kind == existing.Kind &&
        JsonNode.DeepEquals(packageAgent.Config, JsonNode.Parse(existing.ConfigJson)) &&
        SerializedEquals(packageAgent.Outputs, existing.DeclaredOutputs.Select(output => new WorkflowPackageAgentOutput(
            output.Kind,
            output.Description,
            output.PayloadExample is JsonElement payload ? JsonNode.Parse(payload.GetRawText()) : null)));

    private static bool Equivalent(
        WorkflowPackageRole packageRole,
        AgentRole existing,
        IReadOnlyList<AgentRoleToolGrant> existingGrants,
        IReadOnlyList<string> existingSkillNames) =>
        string.Equals(packageRole.DisplayName, existing.DisplayName, StringComparison.Ordinal) &&
        string.Equals(packageRole.Description, existing.Description, StringComparison.Ordinal) &&
        packageRole.IsArchived == existing.IsArchived &&
        SerializedEquals(
            packageRole.ToolGrants.OrderBy(grant => grant.Category).ThenBy(grant => grant.ToolIdentifier, StringComparer.Ordinal),
            existingGrants.OrderBy(grant => grant.Category).ThenBy(grant => grant.ToolIdentifier, StringComparer.Ordinal)
                .Select(grant => new WorkflowPackageRoleGrant(grant.Category, grant.ToolIdentifier))) &&
        packageRole.SkillNames
            .OrderBy(name => name, StringComparer.Ordinal)
            .SequenceEqual(existingSkillNames.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool Equivalent(WorkflowPackageSkill packageSkill, Skill existing) =>
        string.Equals(packageSkill.Body, existing.Body, StringComparison.Ordinal) &&
        packageSkill.IsArchived == existing.IsArchived;

    private static bool Equivalent(
        WorkflowPackageMcpServer packageServer,
        McpServer existing,
        IReadOnlyList<McpServerTool> existingTools) =>
        string.Equals(packageServer.DisplayName, existing.DisplayName, StringComparison.Ordinal) &&
        packageServer.Transport == existing.Transport &&
        string.Equals(packageServer.EndpointUrl, existing.EndpointUrl, StringComparison.Ordinal) &&
        packageServer.HasBearerToken == existing.HasBearerToken &&
        packageServer.IsArchived == existing.IsArchived &&
        SerializedEquals(
            packageServer.Tools.OrderBy(tool => tool.ToolName, StringComparer.Ordinal),
            existingTools.OrderBy(tool => tool.ToolName, StringComparer.Ordinal)
                .Select(tool => new WorkflowPackageMcpServerTool(
                    tool.ToolName,
                    tool.Description,
                    string.IsNullOrWhiteSpace(tool.ParametersJson) ? null : JsonNode.Parse(tool.ParametersJson),
                    tool.IsMutating,
                    tool.SyncedAtUtc)));

    private static bool SerializedEquals<T>(IEnumerable<T> left, IEnumerable<T> right) =>
        JsonSerializer.SerializeToNode(left, JsonSerializerOptions.Web)?.ToJsonString() ==
        JsonSerializer.SerializeToNode(right, JsonSerializerOptions.Web)?.ToJsonString();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime UsePackageDateOrNow(DateTime value, DateTime now) =>
        value == default ? now : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private readonly record struct PackageVersionKey(string Key, int Version);
}
