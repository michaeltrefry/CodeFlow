using CodeFlow.Api.Auth;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Api.Endpoints;

public static class AgentTestEndpoints
{
    private const int MaxToolResultPreviewLength = 2048;

    public static IEndpointRouteBuilder MapAgentTestEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/api/agent-test", RunAgentTestAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.AgentsWrite);

        return routes;
    }

    public sealed record AgentTestRequest(
        string? AgentKey,
        int? AgentVersion,
        string? Input,
        Dictionary<string, string?>? Variables);

    private static async Task RunAgentTestAsync(
        AgentTestRequest request,
        HttpContext httpContext,
        IAgentConfigRepository agentRepository,
        IRoleResolutionService roleResolution,
        Agent agent,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";
        httpContext.Response.StatusCode = StatusCodes.Status200OK;

        await httpContext.Response.WriteAsync(": connected\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);

        if (request is null || string.IsNullOrWhiteSpace(request.AgentKey))
        {
            await WriteEventAsync(httpContext, "error", new { message = "agentKey is required." }, cancellationToken);
            return;
        }

        var agentKey = request.AgentKey.Trim();
        AgentConfig agentConfig;
        try
        {
            var version = request.AgentVersion ?? await agentRepository.GetLatestVersionAsync(agentKey, cancellationToken);
            agentConfig = await agentRepository.GetAsync(agentKey, version, cancellationToken);
        }
        catch (AgentConfigNotFoundException)
        {
            await WriteEventAsync(
                httpContext,
                "error",
                new { message = $"Agent '{agentKey}' version {request.AgentVersion?.ToString() ?? "latest"} not found." },
                cancellationToken);
            return;
        }

        if (agentConfig.Kind == AgentKind.Hitl)
        {
            await WriteEventAsync(
                httpContext,
                "error",
                new { message = "HITL agents cannot be tested directly." },
                cancellationToken);
            return;
        }

        var resolvedTools = await roleResolution.ResolveAsync(agentKey, cancellationToken);

        var invocationConfig = MergeVariables(agentConfig.Configuration, request.Variables);

        await WriteEventAsync(
            httpContext,
            "started",
            new
            {
                agentKey = agentConfig.Key,
                agentVersion = agentConfig.Version,
                provider = invocationConfig.Provider,
                model = invocationConfig.Model,
                timestampUtc = DateTimeOffset.UtcNow
            },
            cancellationToken);

        var observer = new SseInvocationObserver(httpContext);
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            var result = await agent.InvokeAsync(
                invocationConfig,
                request.Input,
                resolvedTools,
                observer,
                cancellationToken);

            await WriteEventAsync(
                httpContext,
                "completed",
                new
                {
                    output = result.Output,
                    decisionKind = result.Decision.PortName,
                    decisionPayload = BuildDecisionPayload(result.Decision),
                    toolCallsExecuted = result.ToolCallsExecuted,
                    tokenUsage = MapUsage(result.TokenUsage),
                    durationMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds,
                    timestampUtc = DateTimeOffset.UtcNow
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // client disconnected; no-op
        }
        catch (Exception exception)
        {
            await WriteEventAsync(
                httpContext,
                "error",
                new
                {
                    message = exception.Message,
                    durationMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
                },
                cancellationToken);
        }
    }

    private static async Task WriteEventAsync(
        HttpContext httpContext,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, AgentTestJson.Options);
        var frame = $"event: {eventName}\ndata: {json}\n\n";
        await httpContext.Response.WriteAsync(frame, cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }

    private static AgentInvocationConfiguration MergeVariables(
        AgentInvocationConfiguration configuration,
        IReadOnlyDictionary<string, string?>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return configuration;
        }

        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (configuration.Variables is not null)
        {
            foreach (var entry in configuration.Variables)
            {
                merged[entry.Key] = entry.Value;
            }
        }

        foreach (var entry in overrides)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }
            merged[entry.Key] = entry.Value;
        }

        return configuration with { Variables = merged };
    }

    private static object? MapUsage(TokenUsage? usage)
        => usage is null ? null : new
        {
            inputTokens = usage.InputTokens,
            outputTokens = usage.OutputTokens,
            totalTokens = usage.TotalTokens
        };

    private static JsonNode? BuildDecisionPayload(AgentDecision decision)
    {
        var json = new JsonObject
        {
            ["portName"] = decision.PortName
        };

        if (decision.Payload is not null)
        {
            json["payload"] = decision.Payload.DeepClone();
        }

        return json;
    }

    internal sealed class SseInvocationObserver : IInvocationObserver
    {
        private readonly HttpContext httpContext;

        public SseInvocationObserver(HttpContext httpContext)
        {
            this.httpContext = httpContext ?? throw new ArgumentNullException(nameof(httpContext));
        }

        public Task OnModelCallStartedAsync(int roundNumber, CancellationToken cancellationToken)
        {
            return WriteEventAsync(
                httpContext,
                "model-call-started",
                new
                {
                    roundNumber,
                    timestampUtc = DateTimeOffset.UtcNow
                },
                cancellationToken);
        }

        public Task OnModelCallCompletedAsync(
            int roundNumber,
            ChatMessage responseMessage,
            TokenUsage? callTokenUsage,
            TokenUsage? cumulativeTokenUsage,
            CancellationToken cancellationToken)
        {
            return WriteEventAsync(
                httpContext,
                "model-call-completed",
                new
                {
                    roundNumber,
                    assistantText = responseMessage.Content,
                    toolCallCount = responseMessage.ToolCalls?.Count ?? 0,
                    callTokenUsage = MapUsage(callTokenUsage),
                    cumulativeTokenUsage = MapUsage(cumulativeTokenUsage),
                    timestampUtc = DateTimeOffset.UtcNow
                },
                cancellationToken);
        }

        public Task OnToolCallStartedAsync(ToolCall call, CancellationToken cancellationToken)
        {
            return WriteEventAsync(
                httpContext,
                "tool-call-started",
                new
                {
                    callId = call.Id,
                    name = call.Name,
                    arguments = call.Arguments,
                    timestampUtc = DateTimeOffset.UtcNow
                },
                cancellationToken);
        }

        public Task OnToolCallCompletedAsync(ToolCall call, ToolResult result, CancellationToken cancellationToken)
        {
            var preview = result.Content;
            var truncated = false;
            if (preview is not null && preview.Length > MaxToolResultPreviewLength)
            {
                preview = preview[..MaxToolResultPreviewLength];
                truncated = true;
            }

            return WriteEventAsync(
                httpContext,
                "tool-call-completed",
                new
                {
                    callId = call.Id,
                    name = call.Name,
                    isError = result.IsError,
                    resultPreview = preview,
                    resultTruncated = truncated,
                    timestampUtc = DateTimeOffset.UtcNow
                },
                cancellationToken);
        }
    }
}

internal static class AgentTestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter()
        }
    };
}
