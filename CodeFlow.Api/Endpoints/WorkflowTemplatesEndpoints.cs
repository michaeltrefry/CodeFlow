using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Api.WorkflowTemplates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeFlow.Api.Endpoints;

/// <summary>
/// S3 (Workflow Authoring DX): HTTP surface for the workflow-template framework.
/// Templates are listed for the editor's "New from template" picker and materialized via a
/// POST that creates the bundled agents + workflows in one transactional handoff.
/// </summary>
public static class WorkflowTemplatesEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowTemplatesEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api/workflow-templates");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);

        group.MapGet("/{id}", GetAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);

        group.MapPost("/{id}/materialize", MaterializeAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);

        return routes;
    }

    private static IResult ListAsync(WorkflowTemplateRegistry registry)
    {
        var summaries = registry.List()
            .Select(t => new WorkflowTemplateSummaryDto(t.Id, t.Name, t.Description, t.Category))
            .ToArray();
        return Results.Ok(summaries);
    }

    private static IResult GetAsync(string id, WorkflowTemplateRegistry registry)
    {
        var template = registry.GetOrDefault(id);
        if (template is null)
        {
            return Results.NotFound();
        }
        return Results.Ok(new WorkflowTemplateSummaryDto(
            template.Id, template.Name, template.Description, template.Category));
    }

    private static async Task<IResult> MaterializeAsync(
        string id,
        MaterializeWorkflowTemplateRequest? request,
        IWorkflowTemplateMaterializer materializer,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.NamePrefix))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["namePrefix"] = ["Name prefix is required."]
            });
        }

        try
        {
            var result = await materializer.MaterializeAsync(
                templateId: id,
                namePrefix: request.NamePrefix!,
                createdBy: currentUser.Id,
                cancellationToken: cancellationToken);

            var dto = new MaterializeWorkflowTemplateResponse(
                EntryWorkflowKey: result.EntryWorkflowKey,
                EntryWorkflowVersion: result.EntryWorkflowVersion,
                CreatedEntities: result.CreatedEntities
                    .Select(e => new MaterializedEntityDto(e.Kind, e.Key, e.Version))
                    .ToArray());

            return Results.Created(
                $"/api/workflows/{result.EntryWorkflowKey}/{result.EntryWorkflowVersion}",
                dto);
        }
        catch (WorkflowTemplateNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [string.IsNullOrEmpty(ex.ParamName) ? "request" : ex.ParamName!] = [ex.Message]
            });
        }
        catch (TemplateKeyCollisionException ex)
        {
            return Results.Conflict(new
            {
                error = ex.Message,
                code = "TemplateKeyCollision",
                conflicts = ex.Conflicts.Select(c => new
                {
                    kind = c.Kind.ToString(),
                    key = c.Key,
                }).ToArray(),
            });
        }
    }
}
