using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeFlow.Api.Endpoints;

public static class AgentRolesEndpoints
{
    private static readonly HashSet<string> HostToolNames = HostToolProvider.GetCatalog()
        .Select(tool => tool.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IEndpointRouteBuilder MapAgentRolesEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api/agent-roles");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentRolesRead);

        group.MapGet("/{id:long}", GetAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentRolesRead);

        group.MapGet("/{id:long}/tools", GetGrantsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentRolesRead);

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentRolesWrite);

        group.MapPut("/{id:long}", UpdateAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentRolesWrite);

        group.MapDelete("/{id:long}", ArchiveAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentRolesWrite);

        group.MapPut("/{id:long}/tools", ReplaceGrantsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentRolesWrite);

        // Agent-role assignment endpoints nested under /api/agents/{key}/roles
        var assignments = routes.MapGroup("/api/agents/{key}/roles");

        assignments.MapGet("/", GetAssignmentsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentsRead);

        assignments.MapPut("/", ReplaceAssignmentsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentsWrite);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        IAgentRoleRepository repository,
        bool? includeArchived,
        CancellationToken cancellationToken)
    {
        var roles = await repository.ListAsync(includeArchived ?? false, cancellationToken);
        return Results.Ok(roles.Select(Map).ToArray());
    }

    private static async Task<IResult> GetAsync(
        long id,
        IAgentRoleRepository repository,
        CancellationToken cancellationToken)
    {
        var role = await repository.GetAsync(id, cancellationToken);
        return role is null ? Results.NotFound() : Results.Ok(Map(role));
    }

    private static async Task<IResult> GetGrantsAsync(
        long id,
        IAgentRoleRepository repository,
        CancellationToken cancellationToken)
    {
        var role = await repository.GetAsync(id, cancellationToken);
        if (role is null)
        {
            return Results.NotFound();
        }

        var grants = await repository.GetGrantsAsync(id, cancellationToken);
        return Results.Ok(grants.Select(g => new AgentRoleGrantResponse(g.Category, g.ToolIdentifier)).ToArray());
    }

    private static async Task<IResult> CreateAsync(
        AgentRoleCreateRequest request,
        IAgentRoleRepository repository,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null)
        {
            errors["body"] = ["Request body is required."];
            return Results.ValidationProblem(errors);
        }

        if (string.IsNullOrWhiteSpace(request.Key))
        {
            errors["key"] = ["Key is required."];
        }
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            errors["displayName"] = ["Display name is required."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var existing = await repository.GetByKeyAsync(request.Key!, cancellationToken);
        if (existing is not null)
        {
            return Results.Conflict(new { error = $"Agent role with key '{request.Key}' already exists." });
        }

        var id = await repository.CreateAsync(new AgentRoleCreate(
            Key: request.Key!,
            DisplayName: request.DisplayName!,
            Description: request.Description,
            CreatedBy: currentUser.Id), cancellationToken);

        var created = await repository.GetAsync(id, cancellationToken);
        return Results.Created($"/api/agent-roles/{id}", Map(created!));
    }

    private static async Task<IResult> UpdateAsync(
        long id,
        AgentRoleUpdateRequest request,
        IAgentRoleRepository repository,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null || string.IsNullOrWhiteSpace(request.DisplayName))
        {
            errors["displayName"] = ["Display name is required."];
            return Results.ValidationProblem(errors);
        }

        var existing = await repository.GetAsync(id, cancellationToken);
        if (existing is null)
        {
            return Results.NotFound();
        }

        try
        {
            await repository.UpdateAsync(id, new AgentRoleUpdate(
                DisplayName: request.DisplayName!,
                Description: request.Description,
                UpdatedBy: currentUser.Id), cancellationToken);
        }
        catch (AgentRoleNotFoundException)
        {
            return Results.NotFound();
        }

        var updated = await repository.GetAsync(id, cancellationToken);
        return Results.Ok(Map(updated!));
    }

    private static async Task<IResult> ArchiveAsync(
        long id,
        IAgentRoleRepository repository,
        CancellationToken cancellationToken)
    {
        var existing = await repository.GetAsync(id, cancellationToken);
        if (existing is null)
        {
            return Results.NotFound();
        }

        try
        {
            await repository.ArchiveAsync(id, cancellationToken);
        }
        catch (AgentRoleNotFoundException)
        {
            return Results.NotFound();
        }
        return Results.NoContent();
    }

    private static async Task<IResult> ReplaceGrantsAsync(
        long id,
        IReadOnlyList<AgentRoleGrantRequest> grants,
        IAgentRoleRepository roleRepository,
        IMcpServerRepository mcpRepository,
        CancellationToken cancellationToken)
    {
        if (grants is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["body"] = ["Grant list is required."]
            });
        }

        var existingRole = await roleRepository.GetAsync(id, cancellationToken);
        if (existingRole is null)
        {
            return Results.NotFound();
        }

        var errors = new Dictionary<string, string[]>();
        var knownServerKeys = (await mcpRepository.ListAsync(includeArchived: true, cancellationToken))
            .Select(s => s.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var domainGrants = new List<AgentRoleToolGrant>(grants.Count);
        for (var i = 0; i < grants.Count; i++)
        {
            var grant = grants[i];
            if (string.IsNullOrWhiteSpace(grant.ToolIdentifier))
            {
                errors[$"grants[{i}].toolIdentifier"] = ["Tool identifier is required."];
                continue;
            }

            if (grant.Category == AgentRoleToolCategory.Host)
            {
                if (!HostToolNames.Contains(grant.ToolIdentifier))
                {
                    errors[$"grants[{i}].toolIdentifier"] = [$"'{grant.ToolIdentifier}' is not a known host tool."];
                    continue;
                }
            }
            else
            {
                var parts = grant.ToolIdentifier.Split(':', 3);
                if (parts.Length != 3 || !string.Equals(parts[0], "mcp", StringComparison.OrdinalIgnoreCase))
                {
                    errors[$"grants[{i}].toolIdentifier"] = ["MCP tool identifier must be 'mcp:<server_key>:<tool_name>'."];
                    continue;
                }

                if (!knownServerKeys.Contains(parts[1]))
                {
                    errors[$"grants[{i}].toolIdentifier"] = [$"MCP server '{parts[1]}' is not registered."];
                    continue;
                }
            }

            domainGrants.Add(new AgentRoleToolGrant(grant.Category, grant.ToolIdentifier!));
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        try
        {
            await roleRepository.ReplaceGrantsAsync(id, domainGrants, cancellationToken);
        }
        catch (AgentRoleNotFoundException)
        {
            return Results.NotFound();
        }

        var persisted = await roleRepository.GetGrantsAsync(id, cancellationToken);
        return Results.Ok(persisted.Select(g => new AgentRoleGrantResponse(g.Category, g.ToolIdentifier)).ToArray());
    }

    private static async Task<IResult> GetAssignmentsAsync(
        string key,
        IAgentRoleRepository repository,
        CancellationToken cancellationToken)
    {
        var normalized = key.Trim();
        var roles = await repository.GetRolesForAgentAsync(normalized, cancellationToken);
        return Results.Ok(roles.Select(Map).ToArray());
    }

    private static async Task<IResult> ReplaceAssignmentsAsync(
        string key,
        AgentAssignmentsRequest request,
        IAgentRoleRepository repository,
        CancellationToken cancellationToken)
    {
        if (request?.RoleIds is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["roleIds"] = ["Role id list is required (use [] to clear assignments)."]
            });
        }

        var normalized = key.Trim();

        try
        {
            await repository.ReplaceAssignmentsAsync(normalized, request.RoleIds, cancellationToken);
        }
        catch (AgentRoleNotFoundException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["roleIds"] = [ex.Message]
            });
        }

        var roles = await repository.GetRolesForAgentAsync(normalized, cancellationToken);
        return Results.Ok(roles.Select(Map).ToArray());
    }

    private static AgentRoleResponse Map(AgentRole role) => new(
        Id: role.Id,
        Key: role.Key,
        DisplayName: role.DisplayName,
        Description: role.Description,
        CreatedAtUtc: role.CreatedAtUtc,
        CreatedBy: role.CreatedBy,
        UpdatedAtUtc: role.UpdatedAtUtc,
        UpdatedBy: role.UpdatedBy,
        IsArchived: role.IsArchived,
        IsSystemManaged: role.IsSystemManaged);
}
