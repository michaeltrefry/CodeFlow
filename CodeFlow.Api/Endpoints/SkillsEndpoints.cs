using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeFlow.Api.Endpoints;

public static class SkillsEndpoints
{
    public static IEndpointRouteBuilder MapSkillsEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api/skills");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.SkillsRead);

        group.MapGet("/{id:long}", GetAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.SkillsRead);

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.SkillsWrite);

        group.MapPut("/{id:long}", UpdateAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.SkillsWrite);

        group.MapDelete("/{id:long}", ArchiveAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.SkillsWrite);

        // Role skill-grant sub-resource, alongside /api/agent-roles/{id}/tools.
        var roleSkills = routes.MapGroup("/api/agent-roles/{id:long}/skills");

        roleSkills.MapGet("/", GetRoleSkillsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentRolesRead);

        roleSkills.MapPut("/", ReplaceRoleSkillsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentRolesWrite);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        ISkillRepository repository,
        bool? includeArchived,
        CancellationToken cancellationToken)
    {
        var skills = await repository.ListAsync(includeArchived ?? false, cancellationToken);
        return Results.Ok(skills.Select(Map).ToArray());
    }

    private static async Task<IResult> GetAsync(
        long id,
        ISkillRepository repository,
        CancellationToken cancellationToken)
    {
        var skill = await repository.GetAsync(id, cancellationToken);
        return skill is null ? Results.NotFound() : Results.Ok(Map(skill));
    }

    private static async Task<IResult> CreateAsync(
        SkillCreateRequest request,
        ISkillRepository repository,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null)
        {
            errors["body"] = ["Request body is required."];
            return Results.ValidationProblem(errors);
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Name is required."];
        }
        if (string.IsNullOrWhiteSpace(request.Body))
        {
            errors["body"] = ["Body is required."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var existing = await repository.GetByNameAsync(request.Name!, cancellationToken);
        if (existing is not null)
        {
            return ApiResults.Conflict($"Skill with name '{request.Name}' already exists.");
        }

        var id = await repository.CreateAsync(new SkillCreate(
            Name: request.Name!,
            Body: request.Body!,
            CreatedBy: currentUser.Id), cancellationToken);

        var created = await repository.GetAsync(id, cancellationToken);
        return Results.Created($"/api/skills/{id}", Map(created!));
    }

    private static async Task<IResult> UpdateAsync(
        long id,
        SkillUpdateRequest request,
        ISkillRepository repository,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["Name is required."];
        }
        if (request is null || string.IsNullOrWhiteSpace(request.Body))
        {
            errors["body"] = ["Body is required."];
        }
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var existing = await repository.GetByNameAsync(request!.Name!, cancellationToken);
        if (existing is not null && existing.Id != id)
        {
            return ApiResults.Conflict($"Skill with name '{request.Name}' already exists.");
        }

        try
        {
            await repository.UpdateAsync(id, new SkillUpdate(
                Name: request.Name!,
                Body: request.Body!,
                UpdatedBy: currentUser.Id), cancellationToken);
        }
        catch (SkillNotFoundException)
        {
            return Results.NotFound();
        }

        var updated = await repository.GetAsync(id, cancellationToken);
        return Results.Ok(Map(updated!));
    }

    private static async Task<IResult> ArchiveAsync(
        long id,
        ISkillRepository repository,
        CancellationToken cancellationToken)
    {
        try
        {
            await repository.ArchiveAsync(id, cancellationToken);
        }
        catch (SkillNotFoundException)
        {
            return Results.NotFound();
        }
        return Results.NoContent();
    }

    private static async Task<IResult> GetRoleSkillsAsync(
        long id,
        IAgentRoleRepository roleRepository,
        CancellationToken cancellationToken)
    {
        var role = await roleRepository.GetAsync(id, cancellationToken);
        if (role is null)
        {
            return Results.NotFound();
        }

        var skillIds = await roleRepository.GetSkillGrantsAsync(id, cancellationToken);
        return Results.Ok(new AgentRoleSkillGrantsResponse(skillIds));
    }

    private static async Task<IResult> ReplaceRoleSkillsAsync(
        long id,
        AgentRoleSkillGrantsRequest request,
        IAgentRoleRepository roleRepository,
        ISkillRepository skillRepository,
        CancellationToken cancellationToken)
    {
        if (request?.SkillIds is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["skillIds"] = ["Skill id list is required (use [] to clear grants)."]
            });
        }

        // Reject grants referencing archived skills — surfaced as a validation error, since the
        // runtime filter would silently drop them otherwise and leave the UI out of sync.
        if (request.SkillIds.Count > 0)
        {
            var distinct = request.SkillIds.Distinct().ToArray();
            var availableArchived = await skillRepository.ListAsync(includeArchived: true, cancellationToken);
            var lookup = availableArchived.ToDictionary(s => s.Id);
            var errors = new Dictionary<string, string[]>();
            for (var i = 0; i < distinct.Length; i++)
            {
                var skillId = distinct[i];
                if (!lookup.TryGetValue(skillId, out var skill))
                {
                    errors[$"skillIds[{i}]"] = [$"Skill {skillId} does not exist."];
                    continue;
                }
                if (skill.IsArchived)
                {
                    errors[$"skillIds[{i}]"] = [$"Skill '{skill.Name}' is archived."];
                }
            }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
        }

        try
        {
            await roleRepository.ReplaceSkillGrantsAsync(id, request.SkillIds, cancellationToken);
        }
        catch (AgentRoleNotFoundException)
        {
            return Results.NotFound();
        }
        catch (SkillNotFoundException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["skillIds"] = [ex.Message]
            });
        }

        var persisted = await roleRepository.GetSkillGrantsAsync(id, cancellationToken);
        return Results.Ok(new AgentRoleSkillGrantsResponse(persisted));
    }

    private static SkillResponse Map(Skill skill) => new(
        Id: skill.Id,
        Name: skill.Name,
        Body: skill.Body,
        CreatedAtUtc: skill.CreatedAtUtc,
        CreatedBy: skill.CreatedBy,
        UpdatedAtUtc: skill.UpdatedAtUtc,
        UpdatedBy: skill.UpdatedBy,
        IsArchived: skill.IsArchived);
}
