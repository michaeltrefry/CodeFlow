using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Api.Validation;
using CodeFlow.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Api.Endpoints;

public static class AgentsEndpoints
{
    public static IEndpointRouteBuilder MapAgentsEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api/agents");

        group.MapGet("/", ListAgentsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentsRead);

        group.MapGet("/{key}/versions", ListAgentVersionsAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentsRead);

        group.MapGet("/{key}/{version:int}", GetAgentVersionAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentsRead);

        group.MapGet("/{key}", GetLatestAgentVersionAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentsRead);

        group.MapPost("/", CreateAgentAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentsWrite);

        group.MapPut("/{key}", CreateAgentVersionAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentsWrite);

        group.MapPost("/{key}/retire", RetireAgentAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentsWrite);

        group.MapPost("/fork", ForkAgentAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentsWrite);

        group.MapGet("/{key}/publish-status", GetPublishStatusAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentsRead);

        group.MapPost("/{key}/publish", PublishForkAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentsWrite);

        group.MapPost("/templates/render-preview", RenderDecisionOutputTemplatePreviewAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentsRead);

        group.MapPost("/templates/render-prompt-preview", RenderPromptTemplatePreviewAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentsRead);

        return routes;
    }

    private static async Task<IResult> RenderPromptTemplatePreviewAsync(
        PromptTemplatePreviewRequest request,
        CodeFlow.Runtime.ContextAssembler contextAssembler,
        CodeFlow.Persistence.IPromptPartialRepository partialRepository,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("Request body is required.");
        }

        // Resolve partial bodies first so we can both pass them to the renderer and surface any
        // missing pins back to the author rather than letting the include silently render the
        // unresolved `{{ include ... }}` token.
        var pinTuples = (request.PartialPins ?? Array.Empty<PromptPartialPinDto>())
            .Where(p => !string.IsNullOrWhiteSpace(p.Key) && p.Version >= 1)
            .Select(p => (p.Key, p.Version))
            .ToArray();

        var resolvedBodies = pinTuples.Length == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : (Dictionary<string, string>)(await partialRepository.ResolveBodiesAsync(pinTuples, cancellationToken))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

        var missing = pinTuples
            .Where(p => !resolvedBodies.ContainsKey(p.Key))
            .Select(p => new PromptTemplatePreviewMissingPartial(p.Key, p.Version))
            .ToArray();

        // If any pin failed to resolve, render against the partials we did get and let the include
        // for the missing key surface as a Scriban runtime error caught below — but include the
        // structured `missing` list so the UI can call out exactly which pin is broken instead of
        // making the author parse the renderer's "Unknown partial" message.

        // P2 mirror: if both review-round bindings are present and the agent didn't pin or include
        // the partial explicitly, the runtime auto-injects @codeflow/last-round-reminder. Surface
        // it as a separate AutoInjection block (rendered against the same scope) so the UI can
        // annotate it `[auto-injected]` rather than trying to fish a Scriban comment out of the
        // rendered output.
        var injection = CodeFlow.Orchestration.AgentInvocationConsumer
            .InjectLastRoundReminderIfApplicable(
                request.SystemPrompt,
                request.PromptTemplate,
                resolvedBodies.Count == 0 ? null : resolvedBodies,
                request.ReviewRound,
                request.OptOutLastRoundReminder ?? false);

        IReadOnlyDictionary<string, string>? partialsForRender = injection.Partials
            ?? (resolvedBodies.Count == 0 ? null : resolvedBodies);

        var variables = CodeFlow.Orchestration.AgentPromptScopeBuilder.BuildAll(
            request.Workflow,
            request.Context,
            request.ReviewRound,
            request.ReviewMaxRounds,
            request.Input);

        PromptTemplatePreviewAutoInjection? autoInjection = null;
        if (injection.Injected)
        {
            string renderedReminderBody;
            try
            {
                var reminderRender = contextAssembler.RenderPreview(
                    systemPrompt: null,
                    promptTemplate: "{{ include \"" + CodeFlow.Persistence.SystemPromptPartials.LastRoundReminderKey + "\" }}",
                    variables,
                    input: request.Input,
                    partialsForRender);
                renderedReminderBody = reminderRender.RenderedPromptTemplate ?? string.Empty;
            }
            catch (CodeFlow.Runtime.PromptTemplateException ex)
            {
                return Results.UnprocessableEntity(new PromptTemplatePreviewErrorResponse(ex.Message, missing));
            }

            autoInjection = new PromptTemplatePreviewAutoInjection(
                Key: CodeFlow.Persistence.SystemPromptPartials.LastRoundReminderKey,
                RenderedBody: renderedReminderBody,
                Reason: "Auto-injected because the agent runs inside a ReviewLoop and did not opt out or include the partial explicitly.");
        }

        CodeFlow.Runtime.PromptPreviewRenderResult render;
        try
        {
            render = contextAssembler.RenderPreview(
                request.SystemPrompt,
                request.PromptTemplate,
                variables,
                request.Input,
                partialsForRender);
        }
        catch (CodeFlow.Runtime.PromptTemplateException ex)
        {
            return Results.UnprocessableEntity(new PromptTemplatePreviewErrorResponse(ex.Message, missing));
        }

        return Results.Ok(new PromptTemplatePreviewResponse(
            RenderedSystemPrompt: render.RenderedSystemPrompt,
            RenderedPromptTemplate: render.RenderedPromptTemplate,
            AutoInjections: autoInjection is null
                ? Array.Empty<PromptTemplatePreviewAutoInjection>()
                : new[] { autoInjection },
            MissingPartials: missing));
    }

    private static IResult RenderDecisionOutputTemplatePreviewAsync(
        DecisionOutputTemplatePreviewRequest request,
        CodeFlow.Runtime.IScribanTemplateRenderer renderer,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrEmpty(request.Template))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["template"] = new[] { "Template must not be empty." }
            });
        }

        var mode = request.Mode ?? DecisionOutputTemplateMode.Hitl;
        var decision = request.Decision ?? string.Empty;
        var outputPortName = request.OutputPortName ?? decision;
        var context = request.Context ?? EndpointDefaults.EmptyJsonElementMap;
        var workflow = request.Workflow ?? EndpointDefaults.EmptyJsonElementMap;

        Scriban.Runtime.ScriptObject scope;
        if (mode == DecisionOutputTemplateMode.Hitl)
        {
            var fields = request.FieldValues ?? EndpointDefaults.EmptyJsonElementMap;
            scope = CodeFlow.Orchestration.DecisionOutputTemplateContext.BuildForHitl(
                decision: decision,
                outputPortName: outputPortName,
                fieldValues: fields,
                reason: request.Reason,
                reasons: request.Reasons,
                actions: request.Actions,
                contextInputs: context,
                workflowInputs: workflow);
        }
        else
        {
            scope = CodeFlow.Orchestration.DecisionOutputTemplateContext.Build(
                decision: decision,
                outputPortName: outputPortName,
                outputText: request.Output ?? string.Empty,
                outputJson: ParseOutputAsStructuredJson(request.Output),
                inputJson: request.Input,
                contextInputs: context,
                workflowInputs: workflow);
        }

        string rendered;
        try
        {
            rendered = renderer.Render(request.Template, scope, cancellationToken);
        }
        catch (CodeFlow.Runtime.PromptTemplateException ex)
        {
            return Results.UnprocessableEntity(new DecisionOutputTemplatePreviewErrorResponse(ex.Message));
        }

        return Results.Ok(new DecisionOutputTemplatePreviewResponse(rendered));
    }

    private static JsonElement? ParseOutputAsStructuredJson(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return document.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static async Task<IResult> ListAgentsAsync(
        CodeFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.Agents
            .AsNoTracking()
            .Where(agent => !agent.IsRetired && agent.OwningWorkflowKey == null)
            .OrderBy(agent => agent.Key)
            .ThenByDescending(agent => agent.Version)
            .ToListAsync(cancellationToken);

        var summaries = entities
            .GroupBy(agent => agent.Key)
            .Select(group =>
            {
                var latest = group.First();
                var configNode = TryParseNode(latest.ConfigJson);
                return new AgentSummaryDto(
                    Key: latest.Key,
                    LatestVersion: latest.Version,
                    Name: ReadString(configNode, "name"),
                    Provider: ReadString(configNode, "provider"),
                    Model: ReadString(configNode, "model"),
                    Type: ReadString(configNode, "type") ?? "agent",
                    LatestCreatedAtUtc: DateTime.SpecifyKind(latest.CreatedAtUtc, DateTimeKind.Utc),
                    LatestCreatedBy: latest.CreatedBy,
                    IsRetired: latest.IsRetired);
            })
            .ToArray();

        return Results.Ok(summaries);
    }

    private static async Task<IResult> ListAgentVersionsAsync(
        string key,
        CodeFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeKey(key);
        var entities = await dbContext.Agents
            .AsNoTracking()
            .Where(agent => agent.Key == normalized)
            .OrderByDescending(agent => agent.Version)
            .ToListAsync(cancellationToken);

        if (entities.Count == 0)
        {
            return Results.NotFound();
        }

        var versions = entities
            .Select(entity => new AgentVersionSummaryDto(
                Key: entity.Key,
                Version: entity.Version,
                CreatedAtUtc: DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc),
                CreatedBy: entity.CreatedBy))
            .ToArray();

        return Results.Ok(versions);
    }

    private static async Task<IResult> GetAgentVersionAsync(
        string key,
        int version,
        CodeFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeKey(key);
        var entity = await dbContext.Agents
            .AsNoTracking()
            .SingleOrDefaultAsync(
                agent => agent.Key == normalized && agent.Version == version,
                cancellationToken);

        if (entity is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(MapVersion(entity));
    }

    private static async Task<IResult> GetLatestAgentVersionAsync(
        string key,
        CodeFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeKey(key);
        var entity = await dbContext.Agents
            .AsNoTracking()
            .Where(agent => agent.Key == normalized)
            .OrderByDescending(agent => agent.Version)
            .FirstOrDefaultAsync(cancellationToken);

        if (entity is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(MapVersion(entity));
    }

    private static async Task<IResult> CreateAgentAsync(
        CreateAgentRequest request,
        IAgentConfigRepository repository,
        CodeFlowDbContext dbContext,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var keyValidation = AgentConfigValidator.ValidateKey(request.Key);
        if (!keyValidation.IsValid)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["key"] = new[] { keyValidation.Error! }
            });
        }

        var configValidation = AgentConfigValidator.ValidateConfig(request.Config);
        if (!configValidation.IsValid)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["config"] = new[] { configValidation.Error! }
            });
        }

        var normalizedKey = NormalizeKey(request.Key!);
        var exists = await dbContext.Agents
            .AsNoTracking()
            .AnyAsync(agent => agent.Key == normalizedKey, cancellationToken);

        if (exists)
        {
            return ApiResults.Conflict($"Agent with key '{normalizedKey}' already exists. Use PUT to add a new version.");
        }

        var configJson = request.Config!.Value.GetRawText();
        var version = await repository.CreateNewVersionAsync(
            normalizedKey,
            configJson,
            currentUser.Id,
            cancellationToken);

        return Results.Created($"/api/agents/{normalizedKey}/{version}", new { key = normalizedKey, version });
    }

    private static async Task<IResult> CreateAgentVersionAsync(
        string key,
        UpdateAgentRequest request,
        IAgentConfigRepository repository,
        CodeFlowDbContext dbContext,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeKey(key);
        if (!IsForkKey(normalizedKey))
        {
            var keyValidation = AgentConfigValidator.ValidateKey(normalizedKey);
            if (!keyValidation.IsValid)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["key"] = new[] { keyValidation.Error! }
                });
            }
        }

        var configValidation = AgentConfigValidator.ValidateConfig(request.Config);
        if (!configValidation.IsValid)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["config"] = new[] { configValidation.Error! }
            });
        }

        var latest = await dbContext.Agents
            .AsNoTracking()
            .Where(agent => agent.Key == normalizedKey)
            .OrderByDescending(agent => agent.Version)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is null)
        {
            return ApiResults.NotFound($"Agent with key '{normalizedKey}' does not exist. Use POST to create it.");
        }

        if (latest.IsRetired)
        {
            return ApiResults.Conflict($"Agent with key '{normalizedKey}' is retired. Create a new agent with a different key.");
        }

        var configJson = request.Config!.Value.GetRawText();
        var version = await repository.CreateNewVersionAsync(
            normalizedKey,
            configJson,
            currentUser.Id,
            cancellationToken);

        return Results.Ok(new { key = normalizedKey, version });
    }

    private const string ForkKeyPrefix = "__fork_";

    private static bool IsForkKey(string key) => key.StartsWith(ForkKeyPrefix, StringComparison.Ordinal);

    private static async Task<IResult> ForkAgentAsync(
        ForkAgentRequest request,
        IAgentConfigRepository repository,
        CodeFlowDbContext dbContext,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("Request body is required.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceKey))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["sourceKey"] = new[] { "Source agent key must not be empty." }
            });
        }

        if (request.SourceVersion is null or < 1)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["sourceVersion"] = new[] { "Source version must be >= 1." }
            });
        }

        if (string.IsNullOrWhiteSpace(request.WorkflowKey))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["workflowKey"] = new[] { "Workflow key must not be empty." }
            });
        }

        var configValidation = AgentConfigValidator.ValidateConfig(request.Config);
        if (!configValidation.IsValid)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["config"] = new[] { configValidation.Error! }
            });
        }

        var normalizedSourceKey = NormalizeKey(request.SourceKey!);
        var sourceEntity = await dbContext.Agents
            .AsNoTracking()
            .SingleOrDefaultAsync(
                agent => agent.Key == normalizedSourceKey && agent.Version == request.SourceVersion!.Value,
                cancellationToken);

        if (sourceEntity is null)
        {
            return ApiResults.NotFound($"Agent '{normalizedSourceKey}' v{request.SourceVersion} does not exist.");
        }

        if (sourceEntity.IsRetired)
        {
            return ApiResults.UnprocessableEntity($"Agent '{normalizedSourceKey}' is retired; forking is not allowed.");
        }

        var configJson = request.Config!.Value.GetRawText();
        var fork = await repository.CreateForkAsync(
            normalizedSourceKey,
            request.SourceVersion!.Value,
            request.WorkflowKey!,
            configJson,
            currentUser.Id,
            cancellationToken);

        return Results.Created(
            $"/api/agents/{fork.Key}/{fork.Version}",
            new ForkAgentResponse(
                Key: fork.Key,
                Version: fork.Version,
                ForkedFromKey: fork.ForkedFromKey!,
                ForkedFromVersion: fork.ForkedFromVersion!.Value,
                OwningWorkflowKey: fork.OwningWorkflowKey!));
    }

    private static async Task<IResult> GetPublishStatusAsync(
        string key,
        IAgentConfigRepository repository,
        CodeFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeKey(key);
        var fork = await dbContext.Agents
            .AsNoTracking()
            .Where(agent => agent.Key == normalizedKey)
            .OrderByDescending(agent => agent.Version)
            .FirstOrDefaultAsync(cancellationToken);

        if (fork is null)
        {
            return ApiResults.NotFound($"Agent '{normalizedKey}' does not exist.");
        }

        if (string.IsNullOrEmpty(fork.ForkedFromKey) || fork.ForkedFromVersion is null)
        {
            return ApiResults.UnprocessableEntity($"Agent '{normalizedKey}' is not a fork.");
        }

        var originalLatest = await dbContext.Agents
            .AsNoTracking()
            .Where(agent => agent.Key == fork.ForkedFromKey)
            .OrderByDescending(agent => agent.Version)
            .Select(agent => (int?)agent.Version)
            .FirstOrDefaultAsync(cancellationToken);

        var isDrift = originalLatest is int latest && latest != fork.ForkedFromVersion.Value;

        return Results.Ok(new PublishForkStatusResponse(
            ForkedFromKey: fork.ForkedFromKey,
            ForkedFromVersion: fork.ForkedFromVersion.Value,
            OriginalLatestVersion: originalLatest,
            IsDrift: isDrift));
    }

    private static async Task<IResult> PublishForkAsync(
        string key,
        PublishForkRequest request,
        IAgentConfigRepository repository,
        CodeFlowDbContext dbContext,
        ICurrentUser currentUser,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("Request body is required.");
        }

        var mode = (request.Mode ?? string.Empty).Trim().ToLowerInvariant();
        if (mode is not ("original" or "new-agent"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["mode"] = new[] { "Mode must be either 'original' or 'new-agent'." }
            });
        }

        var normalizedForkKey = NormalizeKey(key);
        var fork = await dbContext.Agents
            .AsNoTracking()
            .Where(agent => agent.Key == normalizedForkKey)
            .OrderByDescending(agent => agent.Version)
            .FirstOrDefaultAsync(cancellationToken);

        if (fork is null)
        {
            return ApiResults.NotFound($"Agent '{normalizedForkKey}' does not exist.");
        }

        if (string.IsNullOrEmpty(fork.ForkedFromKey) || fork.ForkedFromVersion is null)
        {
            return ApiResults.UnprocessableEntity($"Agent '{normalizedForkKey}' is not a fork.");
        }

        string targetKey;
        if (mode == "original")
        {
            targetKey = fork.ForkedFromKey;

            // Re-check drift at publish time so we don't race with the client.
            var originalLatest = await dbContext.Agents
                .AsNoTracking()
                .Where(agent => agent.Key == fork.ForkedFromKey)
                .OrderByDescending(agent => agent.Version)
                .Select(agent => (int?)agent.Version)
                .FirstOrDefaultAsync(cancellationToken);

            var isDrift = originalLatest is int latest && latest != fork.ForkedFromVersion.Value;
            if (isDrift && request.AcknowledgeDrift != true)
            {
                return Results.Conflict(new
                {
                    error = $"Original agent '{fork.ForkedFromKey}' has moved from v{fork.ForkedFromVersion.Value} to v{originalLatest}. Acknowledge drift or publish as a new agent."
                });
            }
        }
        else
        {
            var keyValidation = AgentConfigValidator.ValidateKey(request.NewKey);
            if (!keyValidation.IsValid)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["newKey"] = new[] { keyValidation.Error! }
                });
            }

            targetKey = NormalizeKey(request.NewKey!);
            var conflict = await dbContext.Agents
                .AsNoTracking()
                .AnyAsync(agent => agent.Key == targetKey, cancellationToken);

            if (conflict)
            {
                return ApiResults.Conflict($"Agent with key '{targetKey}' already exists.");
            }
        }

        var publishedVersion = await repository.CreatePublishedVersionAsync(
            targetKey,
            fork.ConfigJson,
            fork.ForkedFromKey,
            fork.ForkedFromVersion.Value,
            currentUser.Id,
            cancellationToken);

        return Results.Ok(new PublishForkResponse(
            PublishedKey: targetKey,
            PublishedVersion: publishedVersion,
            ForkedFromKey: fork.ForkedFromKey,
            ForkedFromVersion: fork.ForkedFromVersion.Value));
    }

    private static async Task<IResult> RetireAgentAsync(
        string key,
        IAgentConfigRepository repository,
        CancellationToken cancellationToken)
    {
        var keyValidation = AgentConfigValidator.ValidateKey(key);
        if (!keyValidation.IsValid)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["key"] = new[] { keyValidation.Error! }
            });
        }

        var normalizedKey = NormalizeKey(key);
        var found = await repository.RetireAsync(normalizedKey, cancellationToken);
        if (!found)
        {
            return ApiResults.NotFound($"Agent with key '{normalizedKey}' does not exist.");
        }

        return Results.Ok(new { key = normalizedKey, isRetired = true });
    }

    private static AgentVersionDto MapVersion(AgentConfigEntity entity)
    {
        var configNode = TryParseNode(entity.ConfigJson);
        return new AgentVersionDto(
            Key: entity.Key,
            Version: entity.Version,
            Type: ReadString(configNode, "type") ?? "agent",
            Config: configNode,
            CreatedAtUtc: DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc),
            CreatedBy: entity.CreatedBy,
            IsRetired: entity.IsRetired);
    }

    private static string NormalizeKey(string key) => key.Trim();

    private static JsonNode? TryParseNode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonNode? node, string propertyName)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        if (!obj.TryGetPropertyValue(propertyName, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var stringValue))
        {
            return stringValue;
        }

        return value.ToString();
    }
}
