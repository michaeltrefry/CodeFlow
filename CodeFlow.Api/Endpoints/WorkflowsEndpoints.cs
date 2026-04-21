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
            request.MaxRoundsPerRound,
            request.Nodes,
            request.Edges,
            request.Inputs,
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

        var draft = ToDraft(normalizedKey, request.Name!, request.MaxRoundsPerRound, request.Nodes!, request.Edges!, request.Inputs);
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
            request.MaxRoundsPerRound,
            request.Nodes,
            request.Edges,
            request.Inputs,
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

        var draft = ToDraft(normalizedKey, request.Name!, request.MaxRoundsPerRound, request.Nodes!, request.Edges!, request.Inputs);
        var version = await repository.CreateNewVersionAsync(draft, cancellationToken);

        return Results.Ok(new { key = normalizedKey, version });
    }

    private static WorkflowDraft ToDraft(
        string key,
        string name,
        int? maxRoundsPerRound,
        IReadOnlyList<WorkflowNodeDto> nodes,
        IReadOnlyList<WorkflowEdgeDto> edges,
        IReadOnlyList<WorkflowInputDto>? inputs)
    {
        return new WorkflowDraft(
            Key: key,
            Name: name,
            MaxRoundsPerRound: maxRoundsPerRound ?? 3,
            Nodes: nodes
                .Select(node => new WorkflowNodeDraft(
                    Id: node.Id,
                    Kind: node.Kind,
                    AgentKey: node.AgentKey,
                    AgentVersion: node.AgentVersion,
                    Script: node.Script,
                    OutputPorts: node.OutputPorts ?? Array.Empty<string>(),
                    LayoutX: node.LayoutX,
                    LayoutY: node.LayoutY))
                .ToArray(),
            Edges: edges
                .Select((edge, index) => new WorkflowEdgeDraft(
                    FromNodeId: edge.FromNodeId,
                    FromPort: edge.FromPort,
                    ToNodeId: edge.ToNodeId,
                    ToPort: string.IsNullOrWhiteSpace(edge.ToPort) ? WorkflowEdge.DefaultInputPort : edge.ToPort,
                    RotatesRound: edge.RotatesRound,
                    SortOrder: edge.SortOrder == 0 ? index : edge.SortOrder))
                .ToArray(),
            Inputs: inputs is null
                ? Array.Empty<WorkflowInputDraft>()
                : inputs
                    .Select((input, index) => new WorkflowInputDraft(
                        Key: input.Key,
                        DisplayName: input.DisplayName,
                        Kind: input.Kind,
                        Required: input.Required,
                        DefaultValueJson: input.DefaultValueJson,
                        Description: input.Description,
                        Ordinal: input.Ordinal == 0 ? index : input.Ordinal))
                    .ToArray());
    }

    private static WorkflowSummaryDto MapSummary(Workflow workflow) => new(
        Key: workflow.Key,
        LatestVersion: workflow.Version,
        Name: workflow.Name,
        NodeCount: workflow.Nodes.Count,
        EdgeCount: workflow.Edges.Count,
        InputCount: workflow.Inputs.Count,
        CreatedAtUtc: workflow.CreatedAtUtc);

    private static WorkflowDetailDto MapDetail(Workflow workflow) => new(
        Key: workflow.Key,
        Version: workflow.Version,
        Name: workflow.Name,
        MaxRoundsPerRound: workflow.MaxRoundsPerRound,
        CreatedAtUtc: workflow.CreatedAtUtc,
        Nodes: workflow.Nodes
            .Select(node => new WorkflowNodeDto(
                Id: node.Id,
                Kind: node.Kind,
                AgentKey: node.AgentKey,
                AgentVersion: node.AgentVersion,
                Script: node.Script,
                OutputPorts: node.OutputPorts,
                LayoutX: node.LayoutX,
                LayoutY: node.LayoutY))
            .ToArray(),
        Edges: workflow.Edges
            .Select(edge => new WorkflowEdgeDto(
                FromNodeId: edge.FromNodeId,
                FromPort: edge.FromPort,
                ToNodeId: edge.ToNodeId,
                ToPort: edge.ToPort,
                RotatesRound: edge.RotatesRound,
                SortOrder: edge.SortOrder))
            .ToArray(),
        Inputs: workflow.Inputs
            .Select(input => new WorkflowInputDto(
                Key: input.Key,
                DisplayName: input.DisplayName,
                Kind: input.Kind,
                Required: input.Required,
                DefaultValueJson: input.DefaultValueJson,
                Description: input.Description,
                Ordinal: input.Ordinal))
            .ToArray());
}
