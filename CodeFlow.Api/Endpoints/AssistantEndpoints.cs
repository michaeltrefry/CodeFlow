using CodeFlow.Api.Assistant;
using CodeFlow.Api.Assistant.Idempotency;
using CodeFlow.Api.Assistant.Tools;
using CodeFlow.Api.Auth;
using CodeFlow.Api.Dtos;
using CodeFlow.Api.TokenTracking;
using CodeFlow.Api.WorkflowPackages;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Authority;
using CodeFlow.Runtime.Authority.Preflight;
using CodeFlow.Runtime.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        group.MapPost("/conversations/new", CreateConversationAsync);
        group.MapGet("/conversations", ListConversationsAsync);
        group.MapGet("/conversations/{id:guid}", GetConversationAsync);
        group.MapPost("/conversations/{id:guid}/fork", ForkConversationAsync);
        group.MapPost("/conversations/{id:guid}/messages", PostMessageAsync);

        // sc-809 (AR-7): explicit cancel for an in-flight assistant turn. After AR-6 the turn
        // runs in a background Task that survives client disconnect; this is how the chat
        // panel's Cancel button takes a turn down on the server when the user has decided not
        // to wait for it. 204 in all "we did the right thing" cases (already terminal → no-op
        // success); 404 hides existence from non-owners, same convention as the POST.
        group.MapDelete(
            "/conversations/{conversationId:guid}/turns/{idempotencyKey}",
            CancelLiveTurnAsync);

        // sc-795 (AA-4): streams the bytes referenced by an artifact event so the chat
        // panel's Download / View actions can pull a workflow-package draft or snapshot
        // straight from the conversation workspace. Owner-scoped via the same resolver
        // pattern the message endpoints use.
        group.MapGet(
            "/conversations/{conversationId:guid}/artifacts/{eventId:guid}",
            DownloadArtifactAsync);

        // sc-797 (AA-6): preview-and-validate a save from the artifact rail. Mirrors the
        // `save_workflow_package` tool's response shape so the chat panel can reuse its
        // existing chip view-model. Closes the dismissed-chip recovery loop — any artifact
        // the user can see in the rail can be saved straight to the library.
        group.MapPost(
            "/conversations/{conversationId:guid}/artifacts/{eventId:guid}/save",
            PreviewArtifactSaveAsync);

        // HAA-14: aggregated token usage across the user's assistant conversations. Drives the
        // homepage rail's assistant-token chip; lives on the assistant prefix because it scopes
        // per-user, unlike trace-scoped token usage which lives under /api/traces.
        group.MapGet("/token-usage/summary", GetTokenUsageSummaryAsync);

        // HAA-15/16: defaults + available models for the chat composer's per-conversation
        // provider/model selector. Mirrors the no-auth posture of the rest of /api/assistant —
        // demo-mode users see the same options as authenticated users. Returns only model ids;
        // api keys / endpoints stay behind LlmProvidersRead.
        group.MapGet("/defaults", GetAssistantDefaultsAsync);

        return routes;
    }

    /// <summary>
    /// HAA-15/16 — Reports the admin-configured default provider+model for the assistant plus
    /// every (provider, model) pair the operators have configured in
    /// <see cref="ILlmProviderSettingsRepository"/>. The chat composer uses the defaults as the
    /// initial selection and the model list as the available choices for per-conversation
    /// overrides.
    /// </summary>
    private static async Task<IResult> GetAssistantDefaultsAsync(
        IAssistantSettingsRepository assistantSettings,
        ILlmProviderSettingsRepository providerSettings,
        Microsoft.Extensions.Options.IOptions<CodeFlow.Api.Assistant.AssistantOptions> assistantOptions,
        CancellationToken cancellationToken)
    {
        var dbDefaults = await assistantSettings.GetAsync(cancellationToken);
        var allProviders = await providerSettings.GetAllAsync(cancellationToken);

        // Resolve effective defaults: DB admin row → appsettings → first configured (provider+model)
        // pair. We don't throw on a missing api key here — the chat endpoint surfaces that as a
        // turn failure with the actual provider name; the composer just shows "no default yet".
        var options = assistantOptions.Value;
        var defaultProvider = !string.IsNullOrWhiteSpace(dbDefaults?.Provider)
            ? dbDefaults!.Provider
            : !string.IsNullOrWhiteSpace(options.Provider) ? options.Provider : null;
        if (!string.IsNullOrWhiteSpace(defaultProvider) && LlmProviderKeys.IsKnown(defaultProvider))
        {
            defaultProvider = LlmProviderKeys.Canonicalize(defaultProvider);
        }

        var defaultModel = !string.IsNullOrWhiteSpace(dbDefaults?.Model)
            ? dbDefaults!.Model
            : !string.IsNullOrWhiteSpace(options.Model) ? options.Model : null;

        // If no model explicitly configured, fall back to the first listed model on the resolved
        // provider so the composer always has something to display.
        if (string.IsNullOrWhiteSpace(defaultModel) && !string.IsNullOrWhiteSpace(defaultProvider))
        {
            var providerRow = allProviders.FirstOrDefault(p => string.Equals(p.Provider, defaultProvider, StringComparison.OrdinalIgnoreCase));
            defaultModel = providerRow?.Models.FirstOrDefault();
        }

        // Flatten the (provider, model) pairs the operator has configured. Composer renders this
        // as the dropdown options; same shape as the existing /api/llm-providers/models endpoint
        // (which is gated by AgentsRead and not reachable in demo mode).
        var models = allProviders
            .SelectMany(p => p.Models.Select(m => new LlmProviderModelOption(p.Provider, m)))
            .ToArray();

        return Results.Ok(new
        {
            defaultProvider,
            defaultModel,
            // HAA-15/17 — surface the per-conversation cap so the chat composer's token chip can
            // engage its warning + full states. The cap is not sensitive — exceeding it is
            // already user-visible via the turn-refused error message.
            maxTokensPerConversation = dbDefaults?.MaxTokensPerConversation,
            models,
        });
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
        IAssistantArtifactReadRepository artifactRepository,
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
            return ApiResults.BadRequest(ex.Message);
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
        // sc-794 (AA-3): hydrate the inline artifact pills on conversation load. Owner-scoped:
        // the conversation lookup above already enforced ownership via GetOrCreate / scope key,
        // so we can list events directly without a second auth pass.
        var artifactEvents = await artifactRepository.ListByConversationAsync(conversation.Id, cancellationToken);

        return Results.Ok(new
        {
            conversation = MapConversation(conversation),
            messages = messages.Select(MapMessage).ToArray(),
            artifactEvents = artifactEvents.Select(MapArtifactEvent).ToArray(),
        });
    }

    private static async Task<IResult> GetConversationAsync(
        Guid id,
        HttpContext httpContext,
        IAssistantUserResolver userResolver,
        IAssistantConversationRepository repository,
        IAssistantArtifactReadRepository artifactRepository,
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
        var artifactEvents = await artifactRepository.ListByConversationAsync(id, cancellationToken);
        return Results.Ok(new
        {
            conversation = MapConversation(conversation),
            messages = messages.Select(MapMessage).ToArray(),
            artifactEvents = artifactEvents.Select(MapArtifactEvent).ToArray(),
        });
    }

    /// <summary>
    /// sc-795 (AA-4): streams the bytes referenced by an artifact event. The chat panel's
    /// Download / View actions hit this endpoint with the durable event id so the user can
    /// retrieve a workflow-package draft (live bytes from the workspace draft file) or a
    /// snapshot (immutable per-save bytes) at any point — even after dismissing the Save chip.
    ///
    /// Auth mirrors the rest of /api/assistant/conversations/{id}/...: the conversation must
    /// exist AND be owned by the calling user. The events list is owner-scoped by AA-3, so a
    /// non-owner can't even discover an event id; returning 404 here keeps that posture.
    ///
    /// Expired / superseded behavior:
    ///  - Snapshot consumed by apply (AA-1's orphan-guard): the event row carries an
    ///    `expired_at` timestamp + the file is gone. Return 410 Gone with a payload the
    ///    chat panel can flip the pill to expired state on. No 200 + zero bytes.
    ///  - File-on-disk missing for any other reason (workspace rotated, manual cleanup):
    ///    same 410 Gone path. The pill becomes a tombstone.
    ///
    /// Content-Disposition uses `attachment; filename="..."` so the browser default is to
    /// save rather than navigate. The chat panel's Download action can rely on a plain
    /// `&lt;a download&gt;` (or fetch + Blob save); the View action does its own fetch and
    /// renders the bytes in a Monaco side sheet.
    /// </summary>
    private static async Task<IResult> DownloadArtifactAsync(
        Guid conversationId,
        Guid eventId,
        HttpContext httpContext,
        IAssistantUserResolver userResolver,
        IAssistantConversationRepository conversations,
        IAssistantArtifactReadRepository artifactRepository,
        IAssistantWorkspaceProvider workspaceProvider,
        ILogger<AssistantEndpointsLogCategory> logger,
        CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetByIdAsync(conversationId, cancellationToken);
        if (conversation is null)
        {
            // Distinguishable in logs from the other 404 branches so a user "I clicked the
            // chip and got 404" report is debuggable without a debugger.
            logger.LogInformation(
                "Artifact download 404: conversation {ConversationId} not found (URL likely points at a non-conversation guid).",
                conversationId);
            return Results.NotFound();
        }

        var userId = userResolver.Resolve(
            httpContext,
            allowAnonymous: userResolver.IsDemoUser(conversation.UserId));
        if (string.IsNullOrEmpty(userId)
            || !string.Equals(conversation.UserId, userId, StringComparison.Ordinal))
        {
            // Same posture as the message-list path: don't leak existence to non-owners.
            logger.LogInformation(
                "Artifact download 404: caller userId mismatch on conversation {ConversationId} (resolved={ResolvedUserId}, owner={OwnerUserId}).",
                conversationId, userId ?? "(null)", conversation.UserId);
            return Results.NotFound();
        }

        var evt = await artifactRepository.GetAsync(eventId, cancellationToken);
        if (evt is null)
        {
            logger.LogInformation(
                "Artifact download 404: event {EventId} not found (orphaned chip or wrong id).",
                eventId);
            return Results.NotFound();
        }
        if (evt.ConversationId != conversationId)
        {
            // This is the symptom of the carve-out failing — the row was stamped with a
            // conversation id that doesn't match the URL. Today's code paths can't produce
            // this (TryResolveConversationWorkspace pins all artifact recording to the chat),
            // but we log it loudly so a regression shows up.
            logger.LogWarning(
                "Artifact download 404: event {EventId} stamped with conversation {EventConversationId} but URL is {UrlConversationId}. "
                + "This indicates an artifact-recording path bypassed the conversation-workspace carve-out.",
                eventId, evt.ConversationId, conversationId);
            return Results.NotFound();
        }

        if (evt.ExpiredAtUtc is not null)
        {
            // Snapshot already consumed by apply, or otherwise tombstoned — return 410 Gone
            // so the chat panel can flip the pill to expired state. Carry a small JSON body
            // for parity with the rest of the assistant endpoints' error shapes.
            return Results.Json(
                new { state = "expired", message = "This artifact has been consumed and is no longer downloadable." },
                statusCode: StatusCodes.Status410Gone);
        }

        ToolWorkspaceContext workspace;
        try
        {
            workspace = workspaceProvider.GetOrCreateConversationWorkspace(conversationId);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return Results.Problem(
                title: "Assistant workspace unavailable",
                detail: $"Could not access the conversation's workspace: {ex.Message}",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Resolve the on-disk path. Snapshot kinds key off the snapshot id so the immutable
        // bytes are addressable even if the live draft has been overwritten; everything else
        // resolves through the event's relative path.
        string filePath;
        if (evt.SnapshotId is { } snapshotId && evt.Kind == ArtifactEventKind.WorkflowPackageSnapshot)
        {
            filePath = WorkflowPackageDraftStore.ResolveSnapshotPath(workspace, snapshotId);
        }
        else
        {
            // Defense in depth: refuse a relative path that escapes the workspace root via `..`
            // or absolute components. The recorder writes simple file names (draft.cf-...,
            // snapshot-{guid}.cf-...) so a normalized join must stay under workspace.RootPath.
            var combined = Path.GetFullPath(Path.Combine(workspace.RootPath, evt.RelativePath));
            var rootFull = Path.GetFullPath(workspace.RootPath);
            if (!combined.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !string.Equals(combined, rootFull, StringComparison.Ordinal))
            {
                return Results.NotFound();
            }
            filePath = combined;
        }

        if (!File.Exists(filePath))
        {
            // Same shape as expired — file is gone, the metadata row outlives the bytes.
            return Results.Json(
                new { state = "expired", message = "Artifact bytes are no longer available on disk." },
                statusCode: StatusCodes.Status410Gone);
        }

        // Stream the file with attachment Content-Disposition so a plain `<a download>` saves
        // rather than navigates. Content-type is application/json for package artifacts (the
        // only kinds AA-1/AA-2/AP-3 produce); future kinds can refine this.
        var contentType = evt.Kind switch
        {
            ArtifactEventKind.WorkflowPackageDraft => "application/json",
            ArtifactEventKind.WorkflowPackageSnapshot => "application/json",
            ArtifactEventKind.AgentPackageDraft => "application/json",
            ArtifactEventKind.AgentPackageSnapshot => "application/json",
            _ => "application/octet-stream",
        };

        var stream = File.OpenRead(filePath);
        return Results.File(stream, contentType: contentType, fileDownloadName: evt.Name);
    }

    /// <summary>
    /// sc-797 (AA-6): preview-and-validate a "Save to library" from the artifact rail.
    /// Reads the bytes the artifact event references, runs them through the importer's
    /// preview + validation pipeline, and returns the same JSON shape the
    /// <c>save_workflow_package</c> tool emits — <c>buildSaveConfirmationView</c> on the
    /// chat panel reuses its existing logic to render the resulting chip (apply mode for
    /// preview_ok, resolve mode for preview_conflicts, error otherwise).
    ///
    /// The wire shape's <c>packageSource</c> is <c>"artifact"</c> (vs the tool's
    /// <c>"draft"</c> / <c>"inline"</c>) and carries <c>eventId</c> instead of
    /// <c>snapshotId</c>; the chat panel's apply path keys off this discriminator and POSTs
    /// to <c>/api/workflows/package/apply-from-artifact</c>.
    /// </summary>
    private static async Task<IResult> PreviewArtifactSaveAsync(
        Guid conversationId,
        Guid eventId,
        HttpContext httpContext,
        IAssistantUserResolver userResolver,
        IAssistantConversationRepository conversations,
        IAssistantArtifactReadRepository artifactRepository,
        IAssistantWorkspaceProvider workspaceProvider,
        IWorkflowPackageImporter importer,
        CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetByIdAsync(conversationId, cancellationToken);
        if (conversation is null)
        {
            return Results.NotFound();
        }

        var userId = userResolver.Resolve(
            httpContext,
            allowAnonymous: userResolver.IsDemoUser(conversation.UserId));
        if (string.IsNullOrEmpty(userId)
            || !string.Equals(conversation.UserId, userId, StringComparison.Ordinal))
        {
            return Results.NotFound();
        }

        var evt = await artifactRepository.GetAsync(eventId, cancellationToken);
        if (evt is null || evt.ConversationId != conversationId)
        {
            return Results.NotFound();
        }
        if (evt.ExpiredAtUtc is not null)
        {
            return Results.Json(
                new { state = "expired", message = "This artifact has been consumed and is no longer applyable." },
                statusCode: StatusCodes.Status410Gone);
        }
        if (evt.Kind != ArtifactEventKind.WorkflowPackageDraft
            && evt.Kind != ArtifactEventKind.WorkflowPackageSnapshot)
        {
            return ApiResults.BadRequest($"Save is only supported for package-kind artifacts; this event is {evt.Kind}.");
        }

        ToolWorkspaceContext workspace;
        try
        {
            workspace = workspaceProvider.GetOrCreateConversationWorkspace(conversationId);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return Results.Problem(
                title: "Assistant workspace unavailable",
                detail: $"Could not access the conversation's workspace: {ex.Message}",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Resolve the on-disk path through the same helper the apply endpoint uses so a `..`
        // escape lands the same 404 in both paths.
        string filePath;
        if (evt.SnapshotId is { } snapshotId && evt.Kind == ArtifactEventKind.WorkflowPackageSnapshot)
        {
            filePath = WorkflowPackageDraftStore.ResolveSnapshotPath(workspace, snapshotId);
        }
        else
        {
            var combined = Path.GetFullPath(Path.Combine(workspace.RootPath, evt.RelativePath));
            var rootFull = Path.GetFullPath(workspace.RootPath);
            if (!combined.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !string.Equals(combined, rootFull, StringComparison.Ordinal))
            {
                return Results.NotFound();
            }
            filePath = combined;
        }

        if (!File.Exists(filePath))
        {
            return Results.Json(
                new { state = "expired", message = "Artifact bytes are no longer available on disk." },
                statusCode: StatusCodes.Status410Gone);
        }

        WorkflowPackage? package;
        try
        {
            await using var stream = File.OpenRead(filePath);
            package = await JsonSerializer.DeserializeAsync<WorkflowPackage>(
                stream,
                AssistantToolJson.SerializerOptions,
                cancellationToken);
        }
        catch (JsonException ex)
        {
            var payload = new
            {
                status = "invalid",
                message = $"Could not parse the artifact as a workflow package document: {ex.Message}",
                hint = "The artifact bytes on disk failed to deserialize. The conversation's workspace may be corrupted; ask the assistant to re-emit the package.",
            };
            return Results.Ok(payload);
        }
        if (package is null)
        {
            return Results.Ok(new
            {
                status = "invalid",
                message = "The artifact bytes on disk deserialized to null.",
            });
        }

        WorkflowPackageImportPreview preview;
        try
        {
            preview = await importer.PreviewAsync(package, cancellationToken);
        }
        catch (WorkflowPackageResolutionException ex)
        {
            return Results.Ok(new
            {
                status = "invalid",
                message = ex.Message,
                missingReferences = ex.MissingReferences
                    .Select(r => new { kind = r.Kind.ToString(), key = r.Key, version = r.Version, referencedBy = r.ReferencedBy })
                    .ToArray(),
                hint = "The package failed structural validation (schema version, entry-point, or a node carrying an unresolved ref). The artifact may be from an older / incompatible workflow shape.",
            });
        }

        if (preview.CanApply)
        {
            WorkflowPackageValidationResult validation;
            try
            {
                validation = await importer.ValidateAsync(package, cancellationToken);
            }
            catch (WorkflowPackageResolutionException ex)
            {
                return Results.Ok(new
                {
                    status = "invalid",
                    message = ex.Message,
                    hint = "The package was rejected during validation. The artifact bytes may be stale.",
                });
            }
            if (!validation.IsValid)
            {
                return Results.Ok(new
                {
                    status = "invalid",
                    message = "The package would be rejected at save time. Fix the listed errors.",
                    errors = validation.Errors
                        .Select(e => new { workflowKey = e.WorkflowKey, message = e.Message, ruleIds = e.RuleIds ?? Array.Empty<string>() })
                        .ToArray(),
                });
            }
        }

        var summary = new
        {
            status = preview.CanApply ? "preview_ok" : "preview_conflicts",
            entryPoint = new { key = preview.EntryPoint.Key, version = preview.EntryPoint.Version },
            createCount = preview.CreateCount,
            reuseCount = preview.ReuseCount,
            conflictCount = preview.ConflictCount,
            refusedCount = preview.RefusedCount,
            warningCount = preview.WarningCount,
            warnings = preview.Warnings.ToArray(),
            items = preview.Items
                .Select(i => new { kind = i.Kind.ToString(), key = i.Key, version = i.Version, action = i.Action.ToString(), message = i.Message })
                .ToArray(),
            canApply = preview.CanApply,
            // Tells the chat UI which apply path to use:
            //   "artifact" → POST /api/workflows/package/apply-from-artifact with conversationId + eventId
            // The chip's Resolve handoff uses the same shape with packageSource: 'artifact'.
            packageSource = "artifact",
            conversationId,
            eventId,
            artifactName = evt.Name,
            message = preview.CanApply
                ? "Preview validated. Click Save in the chip to apply this artifact to the library."
                : "Preview produced conflicts. The chip's Resolve action navigates to the imports page where you can pick per-row resolutions.",
        };
        return Results.Ok(summary);
    }

    private static async Task<IResult> CreateConversationAsync(
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
            return ApiResults.BadRequest(ex.Message);
        }

        var allowAnonymous = scope.Kind == AssistantConversationScopeKind.Homepage;
        var userId = userResolver.Resolve(httpContext, allowAnonymous);
        if (string.IsNullOrEmpty(userId))
        {
            return Results.Unauthorized();
        }

        var conversation = await repository.CreateAsync(userId, scope, cancellationToken);
        return Results.Ok(new
        {
            conversation = MapConversation(conversation),
            messages = Array.Empty<object>(),
            // sc-794 (AA-3): empty for shape parity with get / get-or-create. A fresh
            // conversation has no artifact events; fork would too (the bytes live in the
            // source conversation's workspace, not the fork's).
            artifactEvents = Array.Empty<object>(),
        });
    }

    /// <summary>
    /// Forks an existing conversation at <paramref name="throughMessageId"/>: creates a new
    /// conversation under the same scope owned by the same user and copies every message up to
    /// and including the named pivot. The user lands on a fresh thread that mirrors the prior
    /// transcript without affecting the original. Auth mirrors <c>GetConversationAsync</c> /
    /// <c>PostMessageAsync</c>: the source conversation must be owned by the calling user, and
    /// demo callers can fork their own homepage threads.
    /// </summary>
    private static async Task<IResult> ForkConversationAsync(
        Guid id,
        ForkConversationRequest request,
        HttpContext httpContext,
        IAssistantUserResolver userResolver,
        IAssistantConversationRepository repository,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ThroughMessageId == Guid.Empty)
        {
            return ApiResults.BadRequest("throughMessageId is required.");
        }

        var source = await repository.GetByIdAsync(id, cancellationToken);
        if (source is null)
        {
            return Results.NotFound();
        }

        var userId = userResolver.Resolve(httpContext, allowAnonymous: userResolver.IsDemoUser(source.UserId));
        if (string.IsNullOrEmpty(userId) || !string.Equals(source.UserId, userId, StringComparison.Ordinal))
        {
            return Results.NotFound();
        }

        var fork = await repository.ForkAsync(id, request.ThroughMessageId, cancellationToken);
        if (fork is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new
        {
            conversation = MapConversation(fork.Conversation),
            messages = fork.Messages.Select(MapMessage).ToArray(),
            artifactEvents = Array.Empty<object>()
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

    /// <summary>
    /// sc-809 (AR-7) — DELETE the in-flight turn for <paramref name="conversationId"/> +
    /// <paramref name="idempotencyKey"/>. Owner-scoped via <see cref="IAssistantUserResolver"/>:
    /// a different caller sees 404 so we don't leak record existence (same convention as the
    /// POST). 204 in the "we did the right thing" cases — InFlight cancelled, already terminal,
    /// or task absent (likely on a different instance).
    /// </summary>
    private static async Task CancelLiveTurnAsync(
        Guid conversationId,
        string idempotencyKey,
        HttpContext httpContext,
        IAssistantUserResolver userResolver,
        IAssistantConversationRepository conversations,
        IAssistantTurnIdempotencyRepository idempotencyRepository,
        IAssistantTurnTaskRegistry turnTaskRegistry,
        ILogger<AssistantEndpointsLogCategory> logger,
        CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetByIdAsync(conversationId, cancellationToken);
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

        // Validate the key matches the same shape the POST expects so a bad cancel can't
        // probe for record existence either.
        if (string.IsNullOrWhiteSpace(idempotencyKey)
            || idempotencyKey.Length < AssistantTurnIdempotencyKeys.MinKeyLength
            || idempotencyKey.Length > AssistantTurnIdempotencyKeys.MaxKeyLength)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var record = await idempotencyRepository.GetAsync(conversationId, idempotencyKey, cancellationToken);
        if (record is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Don't leak record existence to a different user even if the conversation owner
        // matched (defensive — the conversation owner check above already covers this for
        // the homepage-anon case, but a separate ownership check on the record protects
        // against a future world where conversations and idempotency rows can drift).
        if (!string.Equals(record.UserId, userId, StringComparison.Ordinal))
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (record.Status == AssistantTurnIdempotencyStatus.InFlight)
        {
            turnTaskRegistry.Cancel(record.Id);
            logger.LogInformation(
                "Assistant turn cancelled by user recordId={RecordId} conversationId={ConversationId} userId={UserId}",
                record.Id, conversationId, userId);
        }
        // Else: already terminal — nothing to cancel, the row is already a complete event log.

        httpContext.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task PostMessageAsync(
        Guid id,
        SendMessageRequest request,
        HttpContext httpContext,
        IAssistantUserResolver userResolver,
        IAssistantConversationRepository repository,
        AssistantChatService chatService,
        IRefusalEventSink refusalSink,
        IIntentClarityAssessor preflightAssessor,
        IOptions<PreflightOptions> preflightOptions,
        IAssistantTurnIdempotencyCoordinator idempotencyCoordinator,
        IOptions<AssistantTurnIdempotencyOptions> idempotencyOptions,
        AssistantTurnSubscriptionRegistry subscriptionRegistry,
        IAssistantTurnTaskRegistry turnTaskRegistry,
        ILogger<AssistantEndpointsLogCategory> logger,
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
            // F-005: SSE endpoint can't return IResult here (the handler has already taken
            // ownership of the response). Mirrors ApiResults.BadRequest's ProblemDetails shape.
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(
                new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Detail = "content is required.",
                },
                cancellationToken);
            return;
        }

        // sc-525 — read + validate the optional Idempotency-Key header before preflight, so a
        // malformed key never quietly turns into a real LLM call. Absent header → existing
        // behavior unchanged. Preflight runs BEFORE we claim a row: a refused turn never
        // reached the model, so it's safe to refuse subsequent retries deterministically by
        // re-running the assessor.
        var keyValidation = AssistantTurnIdempotencyKeys.TryRead(httpContext, out var idempotencyKey);
        if (keyValidation == AssistantTurnIdempotencyKeys.KeyValidation.Malformed)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(
                new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Detail = $"{AssistantTurnIdempotencyKeys.HeaderName} must be {AssistantTurnIdempotencyKeys.MinKeyLength}-{AssistantTurnIdempotencyKeys.MaxKeyLength} characters of [A-Za-z0-9_-].",
                },
                cancellationToken);
            return;
        }

        // sc-274 phase 2 — ambiguity preflight runs BEFORE the SSE stream opens. Refusing
        // here saves the LLM call entirely and lets the chat panel show focused clarification
        // questions instead of a generic "model couldn't help you" reply. Disabled-mode and
        // assessor failures fall through to the normal stream — preflight is observability
        // for ambiguity, never a hard barrier when the assessor itself misbehaves.
        if (preflightOptions.Value.Enabled)
        {
            IntentClarityAssessment? assessment = null;
            try
            {
                var preflightInput = new AssistantChatPreflightInput(
                    Content: request.Content,
                    HasPageContext: request.PageContext is not null,
                    PageContextKind: request.PageContext?.Kind);
                assessment = preflightAssessor.Assess(PreflightMode.AssistantChat, preflightInput);
            }
            catch
            {
                // Preflight failure is observability-only — never block the chat flow because
                // of an assessor bug. The model call still runs below.
            }

            if (assessment is { IsClear: false })
            {
                await EmitPreflightRefusalAsync(refusalSink, id, assessment, cancellationToken);
                httpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                await httpContext.Response.WriteAsJsonAsync(
                    BuildAssistantPreflightResponse(id, assessment),
                    cancellationToken);
                return;
            }
        }

        if (keyValidation == AssistantTurnIdempotencyKeys.KeyValidation.Valid)
        {
            var requestHash = AssistantTurnIdempotencyKeys.ComputeRequestHash(request);
            var dispatch = await idempotencyCoordinator.DispatchAsync(
                id,
                idempotencyKey!,
                userId,
                requestHash,
                cancellationToken);

            switch (dispatch)
            {
                case AssistantTurnDispatchOutcome.HashMismatch:
                    httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
                    await httpContext.Response.WriteAsJsonAsync(
                        new Microsoft.AspNetCore.Mvc.ProblemDetails
                        {
                            Status = StatusCodes.Status409Conflict,
                            Title = "idempotency-key-conflict",
                            Detail = $"{AssistantTurnIdempotencyKeys.HeaderName} was previously used with a different request body for this conversation.",
                        },
                        cancellationToken);
                    return;

                case AssistantTurnDispatchOutcome.UserMismatch:
                    // Don't confirm row existence to a different caller — surface as not-found.
                    httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;

                case AssistantTurnDispatchOutcome.Replay replay:
                    OpenSseResponse(httpContext);
                    await httpContext.Response.WriteAsync(": connected\n\n", cancellationToken);
                    await httpContext.Response.Body.FlushAsync(cancellationToken);
                    await idempotencyCoordinator.ReplayAsync(
                        replay.Record,
                        (name, payload, ct) => WriteRawEventAsync(httpContext, name, payload, ct),
                        cancellationToken);
                    await WriteRawEventAsync(httpContext, "done", "{}", cancellationToken);
                    return;

                case AssistantTurnDispatchOutcome.WaitThenReplay wait:
                    OpenSseResponse(httpContext);
                    await httpContext.Response.WriteAsync(": connected\n\n", cancellationToken);
                    await httpContext.Response.Body.FlushAsync(cancellationToken);
                    AssistantTurnIdempotencyRecord terminal;
                    try
                    {
                        terminal = await idempotencyCoordinator.WaitForTerminalAsync(
                            wait.Record.Id,
                            idempotencyOptions.Value.WaitTimeout,
                            cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (terminal.Status == AssistantTurnIdempotencyStatus.InFlight)
                    {
                        // sc-806: structured error payload — UI keys off `code` to surface
                        // both Retry and Cancel on this specific failure mode (the previous
                        // turn is still running on the server; retrying just re-enters the
                        // same wait, so a Cancel affordance is essential).
                        await WriteRawEventAsync(
                            httpContext,
                            "error",
                            JsonSerializer.Serialize(new
                            {
                                code = AssistantTurnErrorCodes.TurnStillRunning,
                                message = "Your previous turn is still running on the server. Wait a few seconds and retry, or cancel to start fresh.",
                            }, JsonOptions),
                            cancellationToken);
                        await WriteRawEventAsync(httpContext, "done", "{}", cancellationToken);
                        return;
                    }

                    await idempotencyCoordinator.ReplayAsync(
                        terminal,
                        (name, payload, ct) => WriteRawEventAsync(httpContext, name, payload, ct),
                        cancellationToken);
                    await WriteRawEventAsync(httpContext, "done", "{}", cancellationToken);
                    return;

                case AssistantTurnDispatchOutcome.LiveTail liveTail:
                    // sc-805 (AR-3): retry attaches to the originating recorder's live frame
                    // stream — replay the snapshot of frames already produced, then live-tail
                    // until the producer terminates.
                    OpenSseResponse(httpContext);
                    await httpContext.Response.WriteAsync(": connected\n\n", cancellationToken);
                    await httpContext.Response.Body.FlushAsync(cancellationToken);
                    var snapshotFrames = liveTail.Subscription.Snapshot.Count;
                    var liveFramesDelivered = 0;
                    var detachReason = "completed";
                    try
                    {
                        foreach (var snapshotFrame in liveTail.Subscription.Snapshot)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await WriteRawEventAsync(
                                httpContext,
                                snapshotFrame.Event,
                                snapshotFrame.Payload,
                                cancellationToken);
                        }

                        await foreach (var liveFrame in liveTail.Subscription.ReadAllAsync(cancellationToken))
                        {
                            await WriteRawEventAsync(
                                httpContext,
                                liveFrame.Event,
                                liveFrame.Payload,
                                cancellationToken);
                            liveFramesDelivered++;
                        }

                        await WriteRawEventAsync(httpContext, "done", "{}", cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // Retry client disconnected mid-tail. Detach cleanly — dispose the
                        // subscription via the finally block, do NOT propagate cancellation
                        // to the producer. The originating request keeps running and will
                        // flush its terminal record normally.
                        detachReason = "client-disconnect";
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("fell behind", StringComparison.Ordinal))
                    {
                        // AR-1 slow-subscriber drop. Surface a single error frame + done so
                        // the client sees a clean terminal stream and can fall back to
                        // requesting a fresh turn instead of hanging.
                        detachReason = "fell-behind";
                        await TryWriteErrorAndDoneAsync(
                            httpContext,
                            AssistantTurnErrorCodes.LiveTailFellBehind,
                            "Live-tail subscriber fell behind; please retry.");
                    }
                    catch (TimeoutException)
                    {
                        // AR-1 lifetime ceiling. Same shape as the fell-behind branch.
                        detachReason = "timeout";
                        await TryWriteErrorAndDoneAsync(
                            httpContext,
                            AssistantTurnErrorCodes.LiveTailTimeout,
                            "Live-tail subscriber timed out; please retry.");
                    }
                    finally
                    {
                        await liveTail.Subscription.DisposeAsync();
                        // sc-807 detach log — pairs with the attach log emitted from the
                        // subscription registry. snapshotFrames + liveFramesDelivered together
                        // tell ops how much of the record's stream this retry actually saw.
                        logger.LogInformation(
                            "Live-tail subscriber detached recordId={RecordId} conversationId={ConversationId} reason={DetachReason} snapshotFrames={SnapshotFrames} liveFramesDelivered={LiveFramesDelivered}",
                            liveTail.Record.Id,
                            id,
                            detachReason,
                            snapshotFrames,
                            liveFramesDelivered);
                    }
                    return;

                case AssistantTurnDispatchOutcome.Claimed claimed:
                    // sc-808 (AR-6): hand the producer to the background TurnTaskRegistry,
                    // attach as the first subscriber via the AR-1 subscription registry, and
                    // drain frames as SSE. A client disconnect detaches the subscriber but
                    // never cancels the task — the LLM/tool work runs to terminal status, the
                    // recorder flushes, and a retry within record TTL replays the full stream.
                    await StreamClaimedTurnAsync(
                        claimed,
                        id,
                        request,
                        httpContext,
                        subscriptionRegistry,
                        turnTaskRegistry,
                        logger,
                        cancellationToken);
                    return;
            }
        }

        // No idempotency key supplied: legacy passthrough. Run the producer inline against the
        // request's cancellation token; no recorder, no reattach. Same shape as before AR-1
        // landed since this path doesn't claim a row.
        OpenSseResponse(httpContext);
        await httpContext.Response.WriteAsync(": connected\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
        await foreach (var evt in chatService.SendMessageAsync(
            id,
            request.Content,
            request.PageContext,
            request.Provider,
            request.Model,
            request.WorkspaceOverride,
            cancellationToken))
        {
            var (eventName, payload) = AssistantTurnFrameMapper.Map(evt);
            await WriteRawEventAsync(httpContext, eventName, payload, cancellationToken);
        }
        await WriteRawEventAsync(httpContext, "done", "{}", cancellationToken);
    }

    /// <summary>
    /// sc-808 (AR-6) — Originating-client streaming for a freshly Claimed turn. Subscribes to
    /// the recorder before the producer starts so no frames are missed, hands the producer
    /// factory to the task registry, then drains the subscription as SSE. Disconnect throws
    /// <see cref="OperationCanceledException"/> from <see cref="IAssistantTurnSubscription.ReadAllAsync"/>;
    /// we catch it, dispose the subscription, and return without touching the registered
    /// task. The producer keeps running until completion or fault and flushes the recorder
    /// itself.
    /// </summary>
    private static async Task StreamClaimedTurnAsync(
        AssistantTurnDispatchOutcome.Claimed claimed,
        Guid conversationId,
        SendMessageRequest request,
        HttpContext httpContext,
        AssistantTurnSubscriptionRegistry subscriptionRegistry,
        IAssistantTurnTaskRegistry turnTaskRegistry,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Subscribe BEFORE Start so the subscriber sink is registered atomically with the
        // recorder's empty snapshot — every frame the producer emits will land on this sink.
        var subscription = subscriptionRegistry.TrySubscribe(claimed.Record.Id);
        if (subscription is null)
        {
            // Hard invariant: the recorder registers itself synchronously in its constructor
            // before the coordinator returns Claimed, against the same singleton registry the
            // dispatcher reads from here. Reaching this branch means someone broke that
            // contract — bail loudly so we never silently fall back to a code path that ties
            // producer lifetime to the request (which is exactly what AR-8 is here to prevent).
            logger.LogError(
                "TurnSubscriptionRegistry returned null for a freshly claimed record {RecordId}; this is a programmer error.",
                claimed.Record.Id);
            await claimed.Recorder.FlushAsync(AssistantTurnIdempotencyStatus.Failed, CancellationToken.None);
            httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        try
        {
            // Discard intentional: the registry tracks the task's lifetime and flushes the
            // recorder itself; awaiting here would defeat the disconnect-survives-cancel goal.
            // The chat service must be resolved from the task's fresh DI scope — its
            // dependencies (DbContext, outbox bus, etc.) are scoped, and the originating
            // request's scope disposes the moment the client disconnects.
            _ = turnTaskRegistry.Start(
                claimed.Record.Id,
                (taskScope, taskCt) =>
                {
                    var scopedChat = taskScope.GetRequiredService<AssistantChatService>();
                    return scopedChat.SendMessageAsync(
                        conversationId,
                        request.Content,
                        request.PageContext,
                        request.Provider,
                        request.Model,
                        request.WorkspaceOverride,
                        taskCt);
                },
                claimed.Recorder);

            OpenSseResponse(httpContext);
            await httpContext.Response.WriteAsync(": connected\n\n", cancellationToken);
            await httpContext.Response.Body.FlushAsync(cancellationToken);
            try
            {
                await foreach (var frame in subscription.ReadAllAsync(cancellationToken))
                {
                    await WriteRawEventAsync(httpContext, frame.Event, frame.Payload, cancellationToken);
                }
                await WriteRawEventAsync(httpContext, "done", "{}", cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Originating client disconnected. The producer keeps running; the recorder
                // will flush terminal once the LLM finishes (or the lifetime ceiling fires
                // in AR-7). Do NOT cancel the task here — that's the whole point of AR-6.
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("fell behind", StringComparison.Ordinal))
            {
                // The originating client's own subscription fell behind — surface the same
                // error frame the LiveTail path uses so the client sees a clean terminal.
                await TryWriteErrorAndDoneAsync(
                    httpContext,
                    AssistantTurnErrorCodes.LiveTailFellBehind,
                    "Stream subscriber fell behind; please retry.");
            }
            catch (TimeoutException)
            {
                await TryWriteErrorAndDoneAsync(
                    httpContext,
                    AssistantTurnErrorCodes.LiveTailTimeout,
                    "Stream subscriber timed out; please retry.");
            }
        }
        finally
        {
            await subscription.DisposeAsync();
        }
    }

    private static void OpenSseResponse(HttpContext httpContext)
    {
        httpContext.Response.Headers.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";
    }

    /// <summary>Marker type for an <see cref="ILogger{T}"/> category specific to the assistant
    /// endpoints — the static handler has no natural enclosing type to log under.</summary>
    public sealed class AssistantEndpointsLogCategory { }

    /// <summary>
    /// sc-274 phase 2 — emit a <see cref="RefusalStages.Preflight"/> refusal when the
    /// assistant chat assessor refuses the user message. The refusal carries the per-dimension
    /// scores + clarification questions in <c>DetailJson</c> via the shared
    /// <see cref="PreflightRefusalDetail"/> shape so phase 1 (replay-edit) and phase 2
    /// (assistant chat) write the same schema. Distinguished from phase 1 by
    /// <c>AssistantConversationId</c> being populated instead of <c>TraceId</c>.
    /// </summary>
    private static async Task EmitPreflightRefusalAsync(
        IRefusalEventSink sink,
        Guid conversationId,
        IntentClarityAssessment assessment,
        CancellationToken cancellationToken)
    {
        try
        {
            await sink.RecordAsync(
                new RefusalEvent(
                    Id: Guid.NewGuid(),
                    TraceId: null,
                    AssistantConversationId: conversationId,
                    Stage: RefusalStages.Preflight,
                    Code: "assistant-preflight-ambiguous",
                    Reason: $"Assistant prompt did not meet the {assessment.Mode} clarity threshold "
                            + $"({assessment.OverallScore:0.00} < {assessment.Threshold:0.00}).",
                    Axis: PreflightRefusalDetail.LowestDimensionAxis(assessment),
                    Path: null,
                    DetailJson: PreflightRefusalDetail.Build(assessment).ToJsonString(),
                    OccurredAt: DateTimeOffset.UtcNow),
                cancellationToken);
        }
        catch
        {
            // Refusal recording is observability — never break the primary chat flow on
            // sink failure. The HTTP 422 still flows to the caller.
        }
    }

    private static AssistantPreflightRefusalResponse BuildAssistantPreflightResponse(
        Guid conversationId,
        IntentClarityAssessment assessment) =>
        new(
            ConversationId: conversationId,
            Code: "assistant-preflight-ambiguous",
            Mode: assessment.Mode.ToString(),
            OverallScore: assessment.OverallScore,
            Threshold: assessment.Threshold,
            Dimensions: assessment.Dimensions
                .Select(d => new PreflightDimensionDto(d.Dimension, d.Score, d.Reason))
                .ToArray(),
            MissingFields: assessment.MissingFields,
            ClarificationQuestions: assessment.ClarificationQuestions);

    private static async Task WriteRawEventAsync(HttpContext httpContext, string eventName, string payload, CancellationToken ct)
    {
        var frame = $"event: {eventName}\ndata: {payload}\n\n";
        await httpContext.Response.WriteAsync(frame, ct);
        await httpContext.Response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// sc-805 — best-effort terminal frame pair for the live-tail error paths. Uses
    /// <see cref="CancellationToken.None"/> so a closed-but-still-flushable response can
    /// receive the closing handshake even if the request token has fired; if the client
    /// is already gone the writes throw and we swallow them. <paramref name="code"/> is a
    /// stable identifier from <see cref="AssistantTurnErrorCodes"/> the chat panel keys off
    /// to decide which affordances (Retry / Cancel) belong on the error banner.
    /// </summary>
    private static async Task TryWriteErrorAndDoneAsync(HttpContext httpContext, string code, string message)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { code, message }, JsonOptions);
            await WriteRawEventAsync(httpContext, "error", payload, CancellationToken.None);
            await WriteRawEventAsync(httpContext, "done", "{}", CancellationToken.None);
        }
        catch
        {
            // Client already disconnected — nothing more to do.
        }
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
        inputTokensTotal = conversation.InputTokensTotal,
        outputTokensTotal = conversation.OutputTokensTotal,
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

    /// <summary>
    /// sc-794 (AA-3) — projection used by conversation-load to hydrate the chat panel's
    /// inline artifact pills. Mirrors the live SSE <c>artifact-event</c> payload shape so the
    /// frontend can use one type for both paths, plus pre-computed <c>superseded</c> /
    /// <c>expired</c> booleans so the panel can render the right state without re-walking
    /// the list to find supersession links itself.
    /// </summary>
    private static object MapArtifactEvent(AssistantArtifactEvent evt) => new
    {
        id = evt.Id,
        conversationId = evt.ConversationId,
        sequence = evt.Sequence,
        kind = evt.Kind.ToString(),
        name = evt.Name,
        snapshotId = evt.SnapshotId,
        summary = evt.SummaryJson,
        createdAtUtc = evt.CreatedAtUtc,
        superseded = evt.SupersededByEventId is not null,
        expired = evt.ExpiredAtUtc is not null,
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

public sealed record ForkConversationRequest(Guid ThroughMessageId);

public sealed record ScopeRequest(string? Kind, string? EntityType, string? EntityId);

public sealed record SendMessageRequest(
    string Content,
    AssistantPageContext? PageContext = null,
    string? Provider = null,
    string? Model = null,
    AssistantWorkspaceTarget? WorkspaceOverride = null);
