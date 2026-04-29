using CodeFlow.Api.Assistant;
using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Api.TokenTracking;
using CodeFlow.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
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
        group.MapGet("/conversations", ListConversationsAsync);
        group.MapGet("/conversations/{id:guid}", GetConversationAsync);
        group.MapPost("/conversations/{id:guid}/messages", PostMessageAsync);

        // HAA-14: aggregated token usage across the user's assistant conversations. Drives the
        // homepage rail's assistant-token chip; lives on the assistant prefix because it scopes
        // per-user, unlike trace-scoped token usage which lives under /api/traces.
        group.MapGet("/token-usage/summary", GetTokenUsageSummaryAsync);

        return routes;
    }

    /// <summary>
    /// Default cap on rows returned by the resume-conversation rail. The rail itself only renders
    /// a handful, but we let clients widen up to <see cref="MaxConversationListLimit"/> for any
    /// future "show all my threads" surface.
    /// </summary>
    private const int DefaultConversationListLimit = 20;

    private const int MaxConversationListLimit = 100;

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

    /// <summary>
    /// HAA-14 — Lists the caller's recent assistant conversations for the resume-conversation
    /// rail on the homepage. Mirrors the auth model of get-or-create: authenticated callers see
    /// their own threads; anonymous callers get only the conversations attached to their
    /// <c>cf_anon_id</c> cookie (so demo-mode users still see their single homepage thread).
    /// </summary>
    private static async Task<IResult> ListConversationsAsync(
        HttpContext httpContext,
        IAssistantUserResolver userResolver,
        IAssistantConversationRepository repository,
        CancellationToken cancellationToken,
        int? limit = null)
    {
        // Allow anonymous so the rail renders the demo user's existing homepage thread without
        // forcing a login. Reads only — no risk of data leakage; the resolver scopes everything
        // to the current cookie or claims subject.
        var userId = userResolver.Resolve(httpContext, allowAnonymous: true);
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var clamped = Math.Clamp(limit ?? DefaultConversationListLimit, 1, MaxConversationListLimit);
        var conversations = await repository.ListByUserAsync(userId, clamped, cancellationToken);

        return Results.Ok(new
        {
            conversations = conversations.Select(MapConversationSummary).ToArray()
        });
    }

    /// <summary>
    /// HAA-14 — Aggregated assistant token usage for the caller. Sums the token-usage records
    /// captured against every <see cref="AssistantConversation.SyntheticTraceId"/> the user owns,
    /// returning a "today" rollup (calendar UTC) and an all-time rollup. The homepage rail uses
    /// this to render the assistant-token chip; nothing about the per-trace token panel surfaces
    /// here. Anonymous callers see their own demo conversations' usage.
    /// </summary>
    private static async Task<IResult> GetTokenUsageSummaryAsync(
        HttpContext httpContext,
        IAssistantUserResolver userResolver,
        IAssistantConversationRepository repository,
        ITokenUsageRecordRepository tokenUsageRecords,
        CodeFlowDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var userId = userResolver.Resolve(httpContext, allowAnonymous: true);
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        // Pull the user's conversations with no preview overhead — we only need (id, scope,
        // syntheticTraceId) to build the per-conversation breakdown. Reuse ListByUserAsync at
        // the conservative max so the breakdown is bounded but covers every active thread.
        var conversations = await repository.ListByUserAsync(userId, MaxConversationListLimit, cancellationToken);
        if (conversations.Count == 0)
        {
            return Results.Ok(new AssistantTokenUsageSummaryDto(
                Today: TokenUsageAggregator.EmptyRollup(),
                AllTime: TokenUsageAggregator.EmptyRollup(),
                PerConversation: Array.Empty<AssistantConversationTokenUsageDto>()));
        }

        // Bulk-load all records for all of the user's synthetic traces in one query, then
        // partition in memory. Avoids N round trips for the typical "user has a few threads"
        // case; for users with many entity-scoped conversations the IN list is still bounded by
        // MaxConversationListLimit.
        var syntheticTraceIds = conversations.Select(c => c.SyntheticTraceId).ToArray();
        var allRecords = await dbContext.TokenUsageRecords
            .AsNoTracking()
            .Where(r => syntheticTraceIds.Contains(r.TraceId))
            .ToListAsync(cancellationToken);

        // "Today" is calendar UTC — matches how the rest of the system reasons about time, and
        // avoids bringing per-user timezone into a token chip.
        var todayCutoffUtc = DateTime.UtcNow.Date;

        var allDomain = allRecords.Select(MapTokenUsageRecord).ToArray();
        var todayDomain = allDomain.Where(r => r.RecordedAtUtc >= todayCutoffUtc).ToArray();

        var perConversation = conversations
            .Select(c =>
            {
                var conversationRecords = allDomain
                    .Where(r => r.TraceId == c.SyntheticTraceId)
                    .ToArray();
                return new AssistantConversationTokenUsageDto(
                    ConversationId: c.Id,
                    SyntheticTraceId: c.SyntheticTraceId,
                    Scope: new ScopeDto(
                        Kind: c.ScopeKind.ToString().ToLowerInvariant(),
                        EntityType: c.EntityType,
                        EntityId: c.EntityId),
                    Rollup: TokenUsageAggregator.BuildRollup(conversationRecords));
            })
            .Where(dto => dto.Rollup.CallCount > 0)
            .ToArray();

        return Results.Ok(new AssistantTokenUsageSummaryDto(
            Today: TokenUsageAggregator.BuildRollup(todayDomain),
            AllTime: TokenUsageAggregator.BuildRollup(allDomain),
            PerConversation: perConversation));
    }

    private static TokenUsageRecord MapTokenUsageRecord(TokenUsageRecordEntity entity)
    {
        var usageDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(entity.UsageJson) ? "{}" : entity.UsageJson);
        return new TokenUsageRecord(
            Id: entity.Id,
            TraceId: entity.TraceId,
            NodeId: entity.NodeId,
            InvocationId: entity.InvocationId,
            ScopeChain: Array.Empty<Guid>(),
            Provider: entity.Provider,
            Model: entity.Model,
            RecordedAtUtc: DateTime.SpecifyKind(entity.RecordedAtUtc, DateTimeKind.Utc),
            Usage: usageDocument.RootElement.Clone());
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

    /// <summary>
    /// HAA-14 — Resume-rail projection. Mirrors <see cref="MapConversation"/> for stable id +
    /// scope + timestamps and adds the message-count + first-user-message preview the rail
    /// uses to label entries.
    /// </summary>
    private static object MapConversationSummary(AssistantConversationSummary summary) => new
    {
        id = summary.Id,
        scope = new
        {
            kind = summary.ScopeKind.ToString().ToLowerInvariant(),
            entityType = summary.EntityType,
            entityId = summary.EntityId
        },
        syntheticTraceId = summary.SyntheticTraceId,
        createdAtUtc = summary.CreatedAtUtc,
        updatedAtUtc = summary.UpdatedAtUtc,
        messageCount = summary.MessageCount,
        firstUserMessagePreview = summary.FirstUserMessagePreview
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
