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

        // HAA-6: assistant endpoints intentionally do NOT require authorization. Authenticated
        // callers get their claims subject id; anonymous callers on the homepage scope get a
        // synthetic cookie-backed id (demo mode). Entity-scoped conversations still require
        // authentication — that's enforced inside the handlers via the resolver.
        var group = routes.MapGroup("/api/assistant");

        group.MapPost("/conversations", GetOrCreateConversationAsync);
        group.MapGet("/conversations/{id:guid}", GetConversationAsync);
        group.MapPost("/conversations/{id:guid}/messages", PostMessageAsync);

        return routes;
    }

    private static async Task<IResult> GetOrCreateConversationAsync(
        ConversationScopeRequest request,
        HttpContext httpContext,
        IAssistantUserResolver userResolver,
        IAssistantConversationRepository repository,
        CancellationToken cancellationToken)
    {
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

        // Demo mode is homepage-only. Entity-scoped conversations require a real user because
        // they're advisory views over user-owned data (and even though demo mode disables tool
        // access today, hosting an anon entity-scoped thread would just be confusing).
        var allowAnonymous = scope.Kind == AssistantConversationScopeKind.Homepage;
        var userId = userResolver.Resolve(httpContext, allowAnonymous);
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var conversation = await repository.GetOrCreateAsync(userId, scope, cancellationToken);
        var messages = await repository.ListMessagesAsync(conversation.Id, cancellationToken);

        return Results.Ok(new
        {
            conversation = MapConversation(conversation),
            messages = messages.Select(MapMessage).ToArray()
        });
    }

    private static async Task<IResult> GetConversationAsync(
        Guid id,
        HttpContext httpContext,
        IAssistantUserResolver userResolver,
        IAssistantConversationRepository repository,
        CancellationToken cancellationToken)
    {
        // For reads we never want to mint a fresh anon cookie — that would lose the existing
        // conversation. If there's no auth and no existing cookie, treat as unauthorized.
        var conversation = await repository.GetByIdAsync(id, cancellationToken);
        if (conversation is null)
        {
            return Results.NotFound();
        }

        var userId = userResolver.Resolve(httpContext, allowAnonymous: userResolver.IsDemoUser(conversation.UserId));
        if (string.IsNullOrEmpty(userId) || !string.Equals(conversation.UserId, userId, StringComparison.Ordinal))
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
        IAssistantUserResolver userResolver,
        IAssistantConversationRepository repository,
        AssistantChatService chatService,
        CancellationToken cancellationToken)
    {
        var conversation = await repository.GetByIdAsync(id, cancellationToken);
        if (conversation is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var userId = userResolver.Resolve(httpContext, allowAnonymous: userResolver.IsDemoUser(conversation.UserId));
        if (string.IsNullOrEmpty(userId) || !string.Equals(conversation.UserId, userId, StringComparison.Ordinal))
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

        await foreach (var evt in chatService.SendMessageAsync(id, request.Content, request.PageContext, cancellationToken))
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

public sealed record SendMessageRequest(string Content, AssistantPageContext? PageContext = null);
