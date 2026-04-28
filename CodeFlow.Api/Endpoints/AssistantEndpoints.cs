using CodeFlow.Api.Assistant;
using CodeFlow.Api.Auth;
using CodeFlow.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text.Json;

namespace CodeFlow.Api.Endpoints;

public static class AssistantEndpoints
{
    public static IEndpointRouteBuilder MapAssistantEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api/assistant");

        group.MapPost("/conversations", GetOrCreateConversationAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.Authenticated);

        group.MapGet("/conversations/{id:guid}", GetConversationAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.Authenticated);

        group.MapPost("/conversations/{id:guid}/messages", PostMessageAsync)
            .RequireAuthorization(CodeFlowApiDefaults.Policies.Authenticated);

        return routes;
    }

    private static async Task<IResult> GetOrCreateConversationAsync(
        ConversationScopeRequest request,
        ICurrentUser currentUser,
        IAssistantConversationRepository repository,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrWhiteSpace(currentUser.Id))
        {
            return Results.Unauthorized();
        }

        ArgumentNullException.ThrowIfNull(request);

        AssistantConversationScope scope;
        try
        {
            scope = ParseScope(request);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var conversation = await repository.GetOrCreateAsync(currentUser.Id, scope, cancellationToken);
        var messages = await repository.ListMessagesAsync(conversation.Id, cancellationToken);

        return Results.Ok(new
        {
            conversation = MapConversation(conversation),
            messages = messages.Select(MapMessage).ToArray()
        });
    }

    private static async Task<IResult> GetConversationAsync(
        Guid id,
        ICurrentUser currentUser,
        IAssistantConversationRepository repository,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrWhiteSpace(currentUser.Id))
        {
            return Results.Unauthorized();
        }

        var conversation = await repository.GetByIdAsync(id, cancellationToken);
        if (conversation is null || !string.Equals(conversation.UserId, currentUser.Id, StringComparison.Ordinal))
        {
            return Results.NotFound();
        }

        var messages = await repository.ListMessagesAsync(id, cancellationToken);
        return Results.Ok(new
        {
            conversation = MapConversation(conversation),
            messages = messages.Select(MapMessage).ToArray()
        });
    }

    private static async Task PostMessageAsync(
        Guid id,
        SendMessageRequest request,
        HttpContext httpContext,
        ICurrentUser currentUser,
        IAssistantConversationRepository repository,
        AssistantChatService chatService,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrWhiteSpace(currentUser.Id))
        {
            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var conversation = await repository.GetByIdAsync(id, cancellationToken);
        if (conversation is null || !string.Equals(conversation.UserId, currentUser.Id, StringComparison.Ordinal))
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Content))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "content is required." }, cancellationToken);
            return;
        }

        httpContext.Response.Headers.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";

        await httpContext.Response.WriteAsync(": connected\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);

        await foreach (var evt in chatService.SendMessageAsync(id, request.Content, cancellationToken))
        {
            await WriteEventAsync(httpContext, evt, cancellationToken);
        }

        await WriteRawEventAsync(httpContext, "done", "{}", cancellationToken);
    }

    private static async Task WriteEventAsync(HttpContext httpContext, AssistantTurnEvent evt, CancellationToken ct)
    {
        var (eventName, payload) = evt switch
        {
            UserMessagePersisted m => ("user-message-persisted", JsonSerializer.Serialize(MapMessage(m.Message), JsonOptions)),
            TextDelta t => ("text-delta", JsonSerializer.Serialize(new { delta = t.Delta }, JsonOptions)),
            TokenUsageEmitted u => ("token-usage", JsonSerializer.Serialize(new
            {
                recordId = u.Record.Id,
                provider = u.Record.Provider,
                model = u.Record.Model,
                usage = u.Record.Usage
            }, JsonOptions)),
            AssistantMessagePersisted m => ("assistant-message-persisted", JsonSerializer.Serialize(MapMessage(m.Message), JsonOptions)),
            ToolCallStarted tcs => ("tool-call", JsonSerializer.Serialize(new
            {
                id = tcs.Id,
                name = tcs.Name,
                arguments = tcs.Arguments
            }, JsonOptions)),
            ToolCallCompleted tcc => ("tool-result", JsonSerializer.Serialize(new
            {
                id = tcc.Id,
                name = tcc.Name,
                result = tcc.ResultJson,
                isError = tcc.IsError
            }, JsonOptions)),
            TurnFailed f => ("error", JsonSerializer.Serialize(new { message = f.Message }, JsonOptions)),
            _ => ("unknown", "{}")
        };

        await WriteRawEventAsync(httpContext, eventName, payload, ct);
    }

    private static async Task WriteRawEventAsync(HttpContext httpContext, string eventName, string payload, CancellationToken ct)
    {
        var frame = $"event: {eventName}\ndata: {payload}\n\n";
        await httpContext.Response.WriteAsync(frame, ct);
        await httpContext.Response.Body.FlushAsync(ct);
    }

    private static AssistantConversationScope ParseScope(ConversationScopeRequest request)
    {
        var kind = (request.Scope?.Kind ?? "homepage").Trim().ToLowerInvariant();
        return kind switch
        {
            "homepage" => AssistantConversationScope.Homepage(),
            "entity" => AssistantConversationScope.Entity(
                request.Scope?.EntityType ?? throw new ArgumentException("entityType is required for entity scope."),
                request.Scope?.EntityId ?? throw new ArgumentException("entityId is required for entity scope.")),
            _ => throw new ArgumentException($"Unknown scope kind '{kind}'. Expected 'homepage' or 'entity'.")
        };
    }

    private static object MapConversation(AssistantConversation conversation) => new
    {
        id = conversation.Id,
        scope = new
        {
            kind = conversation.ScopeKind.ToString().ToLowerInvariant(),
            entityType = conversation.EntityType,
            entityId = conversation.EntityId
        },
        syntheticTraceId = conversation.SyntheticTraceId,
        createdAtUtc = conversation.CreatedAtUtc,
        updatedAtUtc = conversation.UpdatedAtUtc
    };

    private static object MapMessage(AssistantMessage message) => new
    {
        id = message.Id,
        sequence = message.Sequence,
        role = message.Role.ToString().ToLowerInvariant(),
        content = message.Content,
        provider = message.Provider,
        model = message.Model,
        invocationId = message.InvocationId,
        createdAtUtc = message.CreatedAtUtc
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed record ConversationScopeRequest(ScopeRequest? Scope);

public sealed record ScopeRequest(string? Kind, string? EntityType, string? EntityId);

public sealed record SendMessageRequest(string Content);
