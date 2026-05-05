using System.Text.Json;
using System.Text.Json.Nodes;
using CodeFlow.Api.Dtos;
using CodeFlow.Api.Mapping;
using CodeFlow.Orchestration.DryRun;
using CodeFlow.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CodeFlow.Api.Endpoints;

public static class WorkflowFixturesEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowFixturesEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var fixtures = routes.MapGroup("/api/workflows/{workflowKey}/fixtures");

        fixtures.MapGet("/", ListAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);
        fixtures.MapGet("/{id:long}", GetAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsRead);
        fixtures.MapPost("/", CreateAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);
        fixtures.MapPut("/{id:long}", UpdateAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);
        fixtures.MapDelete("/{id:long}", DeleteAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);

        // Dry-run lives under the workflow group so authentication scopes are aligned. Requires
        // WorkflowsWrite because it can read fixture mocks (which may include drafts) and is
        // gated to authoring users.
        routes.MapPost("/api/workflows/{workflowKey}/dry-run", DryRunAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.WorkflowsWrite);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        string workflowKey,
        IWorkflowFixtureRepository repository,
        CancellationToken cancellationToken)
    {
        var fixtures = await repository.ListForWorkflowAsync(workflowKey, cancellationToken);
        return Results.Ok(fixtures.Select(f => f.ToSummaryDto()).ToArray());
    }

    private static async Task<IResult> GetAsync(
        string workflowKey,
        long id,
        IWorkflowFixtureRepository repository,
        CancellationToken cancellationToken)
    {
        var fixture = await repository.GetAsync(id, cancellationToken);
        if (fixture is null || !string.Equals(fixture.WorkflowKey, workflowKey, StringComparison.Ordinal))
        {
            return Results.NotFound();
        }
        return Results.Ok(fixture.ToDetailDto());
    }

    private static async Task<IResult> CreateAsync(
        string workflowKey,
        WorkflowFixtureCreateRequest request,
        IWorkflowFixtureRepository repository,
        CancellationToken cancellationToken)
    {
        var errors = ValidateCreate(workflowKey, request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var existing = await repository.GetByKeyAsync(workflowKey, request.FixtureKey, cancellationToken);
        if (existing is not null)
        {
            return Results.Conflict(new
            {
                error = $"Fixture '{request.FixtureKey}' already exists for workflow '{workflowKey}'."
            });
        }

        var entity = new WorkflowFixtureEntity
        {
            WorkflowKey = workflowKey,
            FixtureKey = request.FixtureKey,
            DisplayName = request.DisplayName,
            StartingInput = request.StartingInput,
            MockResponsesJson = SerializeMockResponses(request.MockResponses),
        };

        var created = await repository.CreateAsync(entity, cancellationToken);
        return Results.Created($"/api/workflows/{workflowKey}/fixtures/{created.Id}", created.ToDetailDto());
    }

    private static async Task<IResult> UpdateAsync(
        string workflowKey,
        long id,
        WorkflowFixtureUpdateRequest request,
        IWorkflowFixtureRepository repository,
        CancellationToken cancellationToken)
    {
        var errors = ValidateUpdate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var existing = await repository.GetAsync(id, cancellationToken);
        if (existing is null || !string.Equals(existing.WorkflowKey, workflowKey, StringComparison.Ordinal))
        {
            return Results.NotFound();
        }

        if (!string.Equals(existing.FixtureKey, request.FixtureKey, StringComparison.Ordinal))
        {
            var clash = await repository.GetByKeyAsync(workflowKey, request.FixtureKey, cancellationToken);
            if (clash is not null && clash.Id != id)
            {
                return Results.Conflict(new
                {
                    error = $"Fixture '{request.FixtureKey}' already exists for workflow '{workflowKey}'."
                });
            }
        }

        existing.FixtureKey = request.FixtureKey;
        existing.DisplayName = request.DisplayName;
        existing.StartingInput = request.StartingInput;
        existing.MockResponsesJson = SerializeMockResponses(request.MockResponses);

        var updated = await repository.UpdateAsync(existing, cancellationToken);
        return Results.Ok(updated.ToDetailDto());
    }

    private static async Task<IResult> DeleteAsync(
        string workflowKey,
        long id,
        IWorkflowFixtureRepository repository,
        CancellationToken cancellationToken)
    {
        var existing = await repository.GetAsync(id, cancellationToken);
        if (existing is null || !string.Equals(existing.WorkflowKey, workflowKey, StringComparison.Ordinal))
        {
            return Results.NotFound();
        }
        await repository.DeleteAsync(id, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DryRunAsync(
        string workflowKey,
        DryRunRequestBody body,
        IWorkflowFixtureRepository fixtureRepository,
        DryRunExecutor executor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workflowKey))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["workflowKey"] = ["Workflow key is required."],
            });
        }

        IReadOnlyDictionary<string, IReadOnlyList<DryRunMockResponse>> mocks;
        string? startingInput = body?.StartingInput;
        int? versionOverride = body?.WorkflowVersion;

        if (body?.FixtureId is long fixtureId)
        {
            var fixture = await fixtureRepository.GetAsync(fixtureId, cancellationToken);
            if (fixture is null || !string.Equals(fixture.WorkflowKey, workflowKey, StringComparison.Ordinal))
            {
                return Results.NotFound();
            }
            mocks = DryRunRequest.ParseMockResponses(fixture.MockResponsesJson);
            startingInput ??= fixture.StartingInput;
        }
        else
        {
            var inlineJson = body?.MockResponses?.ToJsonString();
            mocks = DryRunRequest.ParseMockResponses(inlineJson);
        }

        var request = new DryRunRequest(
            WorkflowKey: workflowKey,
            WorkflowVersion: versionOverride,
            StartingInput: startingInput,
            MockResponses: mocks);

        var result = await executor.ExecuteAsync(request, cancellationToken);
        return Results.Ok(result.ToResponse());
    }

    // ---------- helpers ----------

    private static Dictionary<string, string[]> ValidateCreate(string workflowKey, WorkflowFixtureCreateRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(workflowKey))
        {
            errors["workflowKey"] = ["Workflow key is required."];
        }
        if (request is null)
        {
            errors["body"] = ["Request body is required."];
            return errors;
        }
        if (string.IsNullOrWhiteSpace(request.FixtureKey))
        {
            errors["fixtureKey"] = ["Fixture key is required."];
        }
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            errors["displayName"] = ["Display name is required."];
        }
        return errors;
    }

    private static Dictionary<string, string[]> ValidateUpdate(WorkflowFixtureUpdateRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null)
        {
            errors["body"] = ["Request body is required."];
            return errors;
        }
        if (string.IsNullOrWhiteSpace(request.FixtureKey))
        {
            errors["fixtureKey"] = ["Fixture key is required."];
        }
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            errors["displayName"] = ["Display name is required."];
        }
        return errors;
    }

    private static string SerializeMockResponses(JsonNode? mockResponses) =>
        mockResponses?.ToJsonString() ?? "{}";
}
