using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Api.Validation;
using CodeFlow.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Endpoints;

public static class WorkflowsEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowsEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api/workflows");

        group.MapGet("/", ListWorkflowsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);

        group.MapGet("/{key}/versions", ListVersionsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);

        group.MapGet("/{key}/{version:int}", GetVersionAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);

        group.MapGet("/{key}", GetLatestAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);

        group.MapPut("/{key}", CreateVersionAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);

        return routes;
    }

    private static async Task<IResult> ListWorkflowsAsync(
        IWorkflowRepository repository,
        CancellationToken cancellationToken)
    {
        var workflows = await repository.ListLatestAsync(cancellationToken);
        return Results.Ok(workflows.Select(MapSummary).ToArray());
    }

    private static async Task<IResult> ListVersionsAsync(
        string key,
        IWorkflowRepository repository,
        CancellationToken cancellationToken)
    {
        var workflows = await repository.ListVersionsAsync(key, cancellationToken);
        if (workflows.Count == 0)
        {
            return Results.NotFound();
        }

        return Results.Ok(workflows.Select(MapDetail).ToArray());
    }

    private static async Task<IResult> GetVersionAsync(
        string key,
        int version,
        IWorkflowRepository repository,
        CancellationToken cancellationToken)
    {
        try
        {
            var workflow = await repository.GetAsync(key, version, cancellationToken);
            return Results.Ok(MapDetail(workflow));
        }
        catch (WorkflowNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> GetLatestAsync(
        string key,
        IWorkflowRepository repository,
        CancellationToken cancellationToken)
    {
        var workflow = await repository.GetLatestAsync(key, cancellationToken);
        return workflow is null ? Results.NotFound() : Results.Ok(MapDetail(workflow));
    }

    private static async Task<IResult> CreateAsync(
        CreateWorkflowRequest request,
        IWorkflowRepository repository,
        CodeFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var validation = await WorkflowValidator.ValidateAsync(
            request.Key ?? string.Empty,
            request.Name,
            request.StartAgentKey,
            request.EscalationAgentKey,
            request.MaxRoundsPerRound,
            request.Edges,
            dbContext,
            cancellationToken);

        if (!validation.IsValid)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["workflow"] = new[] { validation.Error! }
            });
        }

        var normalizedKey = request.Key!.Trim();
        var existing = await dbContext.Workflows
            .AsNoTracking()
            .AnyAsync(workflow => workflow.Key == normalizedKey, cancellationToken);

        if (existing)
        {
            return Results.Conflict(new { error = $"Workflow '{normalizedKey}' already exists. Use PUT to add a version." });
        }

        var draft = ToDraft(normalizedKey, request.Name!, request.StartAgentKey!, request.EscalationAgentKey, request.MaxRoundsPerRound, request.Edges!);
        var version = await repository.CreateNewVersionAsync(draft, cancellationToken);

        return Results.Created($"/api/workflows/{normalizedKey}/{version}", new { key = normalizedKey, version });
    }

    private static async Task<IResult> CreateVersionAsync(
        string key,
        UpdateWorkflowRequest request,
        IWorkflowRepository repository,
        CodeFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var validation = await WorkflowValidator.ValidateAsync(
            key,
            request.Name,
            request.StartAgentKey,
            request.EscalationAgentKey,
            request.MaxRoundsPerRound,
            request.Edges,
            dbContext,
            cancellationToken);

        if (!validation.IsValid)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["workflow"] = new[] { validation.Error! }
            });
        }

        var normalizedKey = key.Trim();
        var existing = await dbContext.Workflows
            .AsNoTracking()
            .AnyAsync(workflow => workflow.Key == normalizedKey, cancellationToken);

        if (!existing)
        {
            return Results.NotFound(new { error = $"Workflow '{normalizedKey}' does not exist. Use POST to create." });
        }

        var draft = ToDraft(normalizedKey, request.Name!, request.StartAgentKey!, request.EscalationAgentKey, request.MaxRoundsPerRound, request.Edges!);
        var version = await repository.CreateNewVersionAsync(draft, cancellationToken);

        return Results.Ok(new { key = normalizedKey, version });
    }

    private static WorkflowDraft ToDraft(
        string key,
        string name,
        string startAgentKey,
        string? escalationAgentKey,
        int? maxRoundsPerRound,
        IReadOnlyList<WorkflowEdgeDto> edges)
    {
        return new WorkflowDraft(
            Key: key,
            Name: name,
            StartAgentKey: startAgentKey,
            EscalationAgentKey: escalationAgentKey,
            MaxRoundsPerRound: maxRoundsPerRound ?? 3,
            Edges: edges
                .Select((edge, index) => new WorkflowEdgeDraft(
                    FromAgentKey: edge.FromAgentKey,
                    Decision: edge.Decision,
                    Discriminator: edge.Discriminator,
                    ToAgentKey: edge.ToAgentKey,
                    RotatesRound: edge.RotatesRound,
                    SortOrder: edge.SortOrder == 0 ? index : edge.SortOrder))
                .ToArray());
    }

    private static WorkflowSummaryDto MapSummary(Workflow workflow) => new(
        Key: workflow.Key,
        LatestVersion: workflow.Version,
        Name: workflow.Name,
        StartAgentKey: workflow.StartAgentKey,
        EscalationAgentKey: workflow.EscalationAgentKey,
        EdgeCount: workflow.Edges.Count,
        CreatedAtUtc: workflow.CreatedAtUtc);

    private static WorkflowDetailDto MapDetail(Workflow workflow) => new(
        Key: workflow.Key,
        Version: workflow.Version,
        Name: workflow.Name,
        StartAgentKey: workflow.StartAgentKey,
        EscalationAgentKey: workflow.EscalationAgentKey,
        MaxRoundsPerRound: workflow.MaxRoundsPerRound,
        CreatedAtUtc: workflow.CreatedAtUtc,
        Edges: workflow.Edges
            .Select(edge => new WorkflowEdgeDto(
                FromAgentKey: edge.FromAgentKey,
                Decision: edge.Decision,
                Discriminator: edge.Discriminator,
                ToAgentKey: edge.ToAgentKey,
                RotatesRound: edge.RotatesRound,
                SortOrder: edge.SortOrder))
            .ToArray());
}
