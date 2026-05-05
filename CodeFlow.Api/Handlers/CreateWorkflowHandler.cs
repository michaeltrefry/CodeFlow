using CodeFlow.Api.Dtos;
using CodeFlow.Api.Endpoints;
using CodeFlow.Api.Validation;
using CodeFlow.Api.Validation.Pipeline;
using CodeFlow.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Api.Handlers;

/// <summary>
/// Owns <c>POST /api/workflows</c>: legacy structural validation, save-time pipeline rules,
/// "already exists" / "retired" guards, subflow-latest-version resolution, and draft commit.
///
/// <para>
/// Carved out of <see cref="WorkflowsEndpoints"/> (sc-168 / F-004). The pipeline / draft helpers
/// stay in <c>WorkflowsEndpoints</c> as <c>internal static</c> because <c>CreateVersionAsync</c>
/// shares them; lifting both endpoints would extract too much in one PR. Doing this handler
/// first establishes the pattern for the version-creation follow-up.
/// </para>
/// </summary>
public sealed class CreateWorkflowHandler
{
    private readonly IWorkflowRepository repository;
    private readonly IAgentConfigRepository agentRepository;
    private readonly IAgentRoleRepository roleRepository;
    private readonly CodeFlowDbContext dbContext;
    private readonly IAuthoringTelemetry telemetry;
    private readonly WorkflowValidationPipeline pipeline;

    public CreateWorkflowHandler(
        IWorkflowRepository repository,
        IAgentConfigRepository agentRepository,
        IAgentRoleRepository roleRepository,
        CodeFlowDbContext dbContext,
        IAuthoringTelemetry telemetry,
        WorkflowValidationPipeline pipeline)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.agentRepository = agentRepository ?? throw new ArgumentNullException(nameof(agentRepository));
        this.roleRepository = roleRepository ?? throw new ArgumentNullException(nameof(roleRepository));
        this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        this.telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        this.pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    public async Task<IResult> ExecuteAsync(CreateWorkflowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = await WorkflowValidator.ValidateAsync(
            request.Key ?? string.Empty,
            request.Name,
            request.MaxRoundsPerRound,
            request.Nodes,
            request.Edges,
            request.Inputs,
            dbContext,
            repository,
            agentRepository,
            cancellationToken);

        if (!validation.IsValid)
        {
            telemetry.ValidatorBlockedSave(
                request.Key ?? string.Empty,
                new[] { "workflow-validator" });
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["workflow"] = new[] { validation.Error! }
            });
        }

        var pipelineBlock = await WorkflowsEndpoints.RunSaveTimePipelineAsync(
            request.Key ?? string.Empty,
            request.Name,
            request.MaxRoundsPerRound,
            request.Nodes,
            request.Edges,
            request.Inputs,
            dbContext,
            repository,
            agentRepository,
            roleRepository,
            pipeline,
            telemetry,
            cancellationToken,
            request.WorkflowVarsReads,
            request.WorkflowVarsWrites);
        if (pipelineBlock is not null)
        {
            return pipelineBlock;
        }

        var normalizedKey = request.Key!.Trim();
        var existingWorkflow = await repository.GetLatestAsync(normalizedKey, cancellationToken);
        if (existingWorkflow is { IsRetired: true })
        {
            return ApiResults.Conflict($"Workflow '{normalizedKey}' is retired. Create a new workflow with a different key.");
        }

        var existing = await dbContext.Workflows
            .AsNoTracking()
            .AnyAsync(workflow => workflow.Key == normalizedKey, cancellationToken);

        if (existing)
        {
            return ApiResults.Conflict($"Workflow '{normalizedKey}' already exists. Use PUT to add a version.");
        }

        var resolvedNodes = await WorkflowsEndpoints.ResolveSubflowLatestVersionsAsync(request.Nodes!, dbContext, cancellationToken);
        var draft = WorkflowsEndpoints.ToDraft(
            normalizedKey,
            request.Name!,
            request.MaxRoundsPerRound,
            request.Category ?? WorkflowCategory.Workflow,
            request.Tags,
            resolvedNodes,
            request.Edges!,
            request.Inputs,
            request.WorkflowVarsReads,
            request.WorkflowVarsWrites);
        var version = await repository.CreateNewVersionAsync(draft, cancellationToken);

        return Results.Created($"/api/workflows/{normalizedKey}/{version}", new { key = normalizedKey, version });
    }
}
