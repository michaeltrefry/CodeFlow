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

        return routes;
    }

    private static async Task<IResult> ListAgentsAsync(
        CodeFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.Agents
            .AsNoTracking()
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
                    LatestCreatedBy: latest.CreatedBy);
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
            return Results.Conflict(new { error = $"Agent with key '{normalizedKey}' already exists. Use PUT to add a new version." });
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
        var keyValidation = AgentConfigValidator.ValidateKey(key);
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

        var normalizedKey = NormalizeKey(key);
        var exists = await dbContext.Agents
            .AsNoTracking()
            .AnyAsync(agent => agent.Key == normalizedKey, cancellationToken);

        if (!exists)
        {
            return Results.NotFound(new { error = $"Agent with key '{normalizedKey}' does not exist. Use POST to create it." });
        }

        var configJson = request.Config!.Value.GetRawText();
        var version = await repository.CreateNewVersionAsync(
            normalizedKey,
            configJson,
            currentUser.Id,
            cancellationToken);

        return Results.Ok(new { key = normalizedKey, version });
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
            CreatedBy: entity.CreatedBy);
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
