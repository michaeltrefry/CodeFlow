using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using CodeFlow.Api.Assistant.Tools;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using CodeFlow.Runtime.Workspace;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

namespace CodeFlow.Api.Assistant;

public interface ICodeFlowAssistant
{
    /// <summary>
    /// Stream a single assistant turn. <paramref name="overrideProvider"/> and
    /// <paramref name="overrideModel"/> (HAA-16) let the caller pin a non-default
    /// provider/model for this turn — null falls back to the DB-backed admin defaults
    /// (HAA-15) and finally to <see cref="AssistantOptions"/>.
    /// </summary>
    IAsyncEnumerable<AssistantStreamItem> AskAsync(
        string userMessage,
        IReadOnlyList<AssistantMessage> history,
        ToolAccessPolicy? toolPolicy = null,
        AssistantPageContext? pageContext = null,
        string? overrideProvider = null,
        string? overrideModel = null,
        Guid conversationId = default,
        AssistantWorkspaceTarget? workspaceOverride = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Streaming chat-loop service. Routes requests to the configured provider's official SDK
/// (Anthropic or OpenAI; LMStudio reuses the OpenAI client with a custom endpoint). Each turn
/// runs a bounded tool-calling loop: stream the model's response, dispatch any tool calls,
/// feed the results back, and continue until the model produces a final answer (no more tool
/// calls) or <see cref="AssistantRuntimeConfig.MaxTurns"/> is reached.
/// </summary>
/// <remarks>
/// Mirrors the architecture of CodeGraph's <c>GraphAssistant</c> (provider routing, SDK-native
/// streaming, per-call key/endpoint resolution). Only the FINAL assistant text is persisted to
/// <c>assistant_messages</c> — tool_use / tool_result blocks are transient: they live within the
/// turn so the model can act on them, then are discarded. On the next turn, if the user's new
/// question depends on prior tool data, the model tool-calls again. This keeps the persistence
/// schema simple at the cost of some redundant tool calls across turns.
/// </remarks>
public sealed class CodeFlowAssistant(
    IAssistantSettingsResolver settingsResolver,
    ILlmProviderSettingsRepository providerSettings,
    IAssistantSystemPromptProvider systemPromptProvider,
    AssistantToolDispatcher toolDispatcher,
    IAnthropicClient anthropicClient,
    IRoleResolutionService roleResolution,
    AgentRoleToolFactory roleToolFactory,
    WorkflowDraftAssistantToolFactory workflowDraftToolFactory,
    IAssistantWorkspaceProvider workspaceProvider,
    IAssistantConversationRepository conversations,
    ILogger<CodeFlowAssistant> logger) : ICodeFlowAssistant
{
    public async IAsyncEnumerable<AssistantStreamItem> AskAsync(
        string userMessage,
        IReadOnlyList<AssistantMessage> history,
        ToolAccessPolicy? toolPolicy = null,
        AssistantPageContext? pageContext = null,
        string? overrideProvider = null,
        string? overrideModel = null,
        Guid conversationId = default,
        AssistantWorkspaceTarget? workspaceOverride = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(history);

        var config = await settingsResolver.ResolveAsync(overrideProvider, overrideModel, cancellationToken);

        // CE-1: split the system prompt into a stable "cacheable" head and a "volatile" tail so
        // the same prefix repeats verbatim across every iteration of the in-turn tool loop AND
        // across turns of the same conversation. The cacheable head is what we mark with
        // `cache_control: ephemeral` on the Anthropic side and what OpenAI's automatic prefix
        // cache keys off of. Anything that varies per-turn (budget block, page context, workspace
        // switch notice) lands AFTER the cache breakpoint so it doesn't invalidate the prefix.
        //
        // Order within the cacheable head: curated knowledge base, then operator overlay.
        // Order within the volatile tail: workspace-unavailable notice, workspace-switch notice,
        // page context, budget block. The model sees them right after the cached body, before
        // any user message, so the conceptual "you read this last" effect is preserved.
        var cacheableSystemPrompt = await systemPromptProvider.GetSystemPromptAsync(cancellationToken);
        // Operator-authored instructions overlay (LLM Providers admin → Assistant defaults).
        // Stable per-conversation (admin-tunable) → belongs in the cacheable block.
        var operatorInstructions = config.OperatorInstructions;
        if (!string.IsNullOrWhiteSpace(operatorInstructions))
        {
            var overlay = BuildOperatorInstructionsBlock(operatorInstructions);
            cacheableSystemPrompt = string.IsNullOrEmpty(cacheableSystemPrompt)
                ? overlay
                : cacheableSystemPrompt + "\n\n" + overlay;
        }

        // Volatile tail: per-turn content the cache must NOT include.
        var volatileSystemPrompt = string.Empty;
        // HAA-8: structured page-context block so the model can resolve "this trace",
        // "this node", etc. without the user pasting IDs. Per-turn — most recent value wins.
        var contextBlock = AssistantPageContextFormatter.FormatAsSystemMessage(pageContext);
        if (!string.IsNullOrEmpty(contextBlock))
        {
            volatileSystemPrompt = contextBlock;
        }
        // Per-turn budget block: tells the model the tool-loop cap so it can pace itself rather
        // than trickling tool calls until the harness aborts the turn. Lives in the volatile tail
        // because MaxTurns is admin-tunable and config-resolved per turn.
        var budgetBlock = BuildTurnBudgetBlock(config.MaxTurns);
        volatileSystemPrompt = string.IsNullOrEmpty(volatileSystemPrompt)
            ? budgetBlock
            : volatileSystemPrompt + "\n\n" + budgetBlock;

        // HAA-19 — Resolve the host-tool workspace based on the per-turn override. Detect a switch
        // versus the previously-persisted signature and prepend a one-shot notice to the system
        // prompt so the model can re-orient (different repos / files visible).
        //
        // Gate the resolution on actual need: a workspace is meaningful when (a) an agent role
        // is assigned (its host tools operate against the dir), (b) the user explicitly switched
        // to a trace workdir, or (c) we have a conversation id (the workflow-package draft tools
        // operate against the per-conversation dir; they degrade silently when creation fails so
        // dev environments without a writable root simply lose those tools for the turn).
        var workspaceNeeded = workspaceOverride is not null
            || (config.AssignedAgentRoleId is { } needRoleId && needRoleId > 0)
            || conversationId != Guid.Empty;
        ResolvedWorkspace? resolvedWorkspace = null;
        if (workspaceNeeded)
        {
            try
            {
                resolvedWorkspace = ResolveWorkspace(workspaceOverride, conversationId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The host-tool workspace couldn't be created (e.g. AssistantWorkspaceRoot isn't a
                // writable mount in this deployment). Don't fail the turn — degrade to built-in
                // tools and tell the model why so it can explain to the user instead of pretending
                // it has tools it can't run.
                logger.LogWarning(ex,
                    "Failed to resolve assistant workspace for conversation {ConversationId}; role host tools will be unavailable for this turn.",
                    conversationId);
                var degradationNotice = BuildWorkspaceUnavailableNotice(ex.Message);
                volatileSystemPrompt = string.IsNullOrEmpty(volatileSystemPrompt)
                    ? degradationNotice
                    : degradationNotice + "\n\n" + volatileSystemPrompt;
            }
        }
        if (conversationId != Guid.Empty && resolvedWorkspace is { } rw)
        {
            var conversation = await conversations.GetByIdAsync(conversationId, cancellationToken);
            var newSignature = rw.Signature;
            if (conversation is not null
                && !string.Equals(conversation.ActiveWorkspaceSignature, newSignature, StringComparison.Ordinal))
            {
                var notice = BuildWorkspaceSwitchNotice(conversation.ActiveWorkspaceSignature, rw);
                volatileSystemPrompt = string.IsNullOrEmpty(volatileSystemPrompt)
                    ? notice
                    : notice + "\n\n" + volatileSystemPrompt;
                await conversations.SetActiveWorkspaceSignatureAsync(conversationId, newSignature, cancellationToken);
            }
        }

        // Merge the built-in IAssistantTool registry with adapters for the assigned agent role's
        // host + MCP grants (if any). The merge is gated only on a role being assigned — host
        // tools require a resolved workspace (the factory drops them when null), but MCP tools do
        // not (the MCP server runs the work), so a workspace creation failure must NOT suppress
        // MCP grants too. Demo policy still wins — if NoTools is in effect the merge is filtered
        // down to nothing downstream.
        var dispatcher = toolDispatcher;
        if (config.AssignedAgentRoleId is { } roleId && roleId > 0)
        {
            try
            {
                var resolved = await roleResolution.ResolveByRoleAsync(roleId, cancellationToken);
                var roleTools = roleToolFactory.Build(resolvedWorkspace?.Context, resolved);
                if (roleTools.Count > 0)
                {
                    dispatcher = MergeDispatcher(toolDispatcher, roleTools);
                }
                if (resolved.EnableHostTools && resolvedWorkspace is null)
                {
                    logger.LogWarning(
                        "Agent role {RoleId} grants host tools, but no workspace was resolved for assistant conversation {ConversationId}. MCP grants (if any) are still active; host tools are unavailable for this turn.",
                        roleId, conversationId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Role resolution failures shouldn't crash the turn — log and fall back to the
                // built-in registry so the user still gets an assistant response.
                logger.LogWarning(ex,
                    "Failed to resolve agent role {RoleId} for assistant conversation {ConversationId}; falling back to built-in tools only.",
                    roleId, conversationId);
            }
        }

        // Workflow package draft tools. The factory always returns a workspace-aware
        // SaveWorkflowPackageTool (overrides the DI-registered workspace-blind one) and, when
        // a workspace resolves, the four draft tools (set/get/patch/clear). Use the override
        // merge so the factory's save tool replaces the DI fallback in the LLM's view.
        var draftTools = workflowDraftToolFactory.Build(resolvedWorkspace?.Context);
        if (draftTools.Count > 0)
        {
            dispatcher = MergeDispatcherWithOverride(dispatcher, draftTools);
        }

        var allowedTools = FilterTools(dispatcher.Tools, toolPolicy);

        IAsyncEnumerable<AssistantStreamItem> stream = config.Provider switch
        {
            LlmProviderKeys.Anthropic => AskAnthropicAsync(config, cacheableSystemPrompt, volatileSystemPrompt, userMessage, history, allowedTools, dispatcher, cancellationToken),
            LlmProviderKeys.OpenAi or LlmProviderKeys.LmStudio => AskOpenAiAsync(config, cacheableSystemPrompt, volatileSystemPrompt, userMessage, history, allowedTools, dispatcher, cancellationToken),
            _ => throw new InvalidOperationException(
                $"Assistant provider '{config.Provider}' is not supported. Expected one of: anthropic, openai, lmstudio.")
        };

        await foreach (var item in stream.WithCancellation(cancellationToken))
        {
            yield return item;
        }
    }

    private static IReadOnlyCollection<IAssistantTool> FilterTools(
        IReadOnlyCollection<IAssistantTool> tools,
        ToolAccessPolicy? policy)
    {
        if (policy is null)
        {
            return tools;
        }

        var filtered = tools.Where(t => policy.AllowsTool(t.Name)).ToArray();
        return filtered;
    }

    /// <summary>
    /// HAA-19 — resolves the workspace the assistant's host tools should operate against. Returns
    /// null if there's no plausible workspace (no conversation id) so callers can short-circuit.
    /// A trace override that points at a missing dir is logged and degrades to the conversation
    /// workspace; the UI is responsible for not offering "switch to trace" when the workdir was
    /// already swept, but a stale tab shouldn't crash the turn.
    /// </summary>
    private ResolvedWorkspace? ResolveWorkspace(AssistantWorkspaceTarget? target, Guid conversationId)
    {
        if (conversationId == Guid.Empty)
        {
            return null;
        }

        if (target is { Kind: AssistantWorkspaceKind.Trace, TraceId: { } traceId } && traceId != Guid.Empty)
        {
            try
            {
                var ctx = workspaceProvider.GetTraceWorkspace(traceId);
                return new ResolvedWorkspace(ctx, $"trace:{traceId:N}", traceId);
            }
            catch (DirectoryNotFoundException)
            {
                logger.LogWarning(
                    "Assistant conversation {ConversationId} requested trace {TraceId} workspace, but the dir is missing; falling back to conversation workspace.",
                    conversationId, traceId);
            }
        }

        // Wrap conversation-workspace creation so a non-writable root (typically a dev environment
        // where the default `/app/codeflow/assistant` container path doesn't exist) doesn't crash
        // the turn. Log a clear warning pointing at the env-var override and degrade to no host
        // tools — the LLM still answers, just without per-conversation file access.
        try
        {
            var conversationCtx = workspaceProvider.GetOrCreateConversationWorkspace(conversationId);
            return new ResolvedWorkspace(conversationCtx, "conversation", null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex,
                "Could not create the per-conversation workspace for assistant conversation {ConversationId}. "
                + "Set the Workspace__AssistantWorkspaceRoot environment variable to a writable directory (default '/app/codeflow/assistant' is a container path). "
                + "Continuing without host tools for this turn.",
                conversationId);
            return null;
        }
    }

    private static string BuildTurnBudgetBlock(int maxTurns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<turn-budget>");
        sb.Append("You are bounded to ").Append(maxTurns).AppendLine(" tool-call turns per user message. Spend them deliberately:");
        sb.AppendLine("- Plan before calling tools. Decide what you actually need, then make the minimum set of calls.");
        sb.AppendLine("- Batch independent tool calls in parallel where the API allows it instead of chaining one-at-a-time.");
        sb.AppendLine("- Do not repeat a call you already made this turn — earlier tool results are still in context.");
        sb.AppendLine("- Stop calling tools and emit a final answer as soon as you have what you need. Don't call tools to re-confirm what you already know.");
        sb.AppendLine("- If the work would exceed the budget, return a partial answer summarizing what you found and the next step you'd take, rather than running out of turns silently.");
        sb.Append("</turn-budget>");
        return sb.ToString();
    }

    private static string BuildOperatorInstructionsBlock(string instructions)
    {
        // Wrap the operator overlay in a clearly-labeled block so the model can distinguish
        // platform-curated knowledge from instance-specific guidance. Trim the value defensively;
        // the repository normalizes blanks to null but operator pastes can still carry trailing
        // whitespace.
        var sb = new StringBuilder();
        sb.AppendLine("<operator-instructions>");
        sb.AppendLine("Additional guidance from this CodeFlow instance's operator. These instructions");
        sb.AppendLine("supplement the curated system prompt above and may describe extra tools that have");
        sb.AppendLine("been granted to you (via the assigned agent role), scope rules, or persona tweaks.");
        sb.AppendLine("Treat this block as authoritative; it overrides the curated prompt where they");
        sb.AppendLine("conflict.");
        sb.AppendLine();
        sb.AppendLine(instructions.Trim());
        sb.Append("</operator-instructions>");
        return sb.ToString();
    }

    private static string BuildWorkspaceUnavailableNotice(string reason)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<workspace-unavailable>");
        sb.AppendLine("The host-tool workspace could not be created for this turn:");
        sb.Append("Reason: ").AppendLine(reason);
        sb.AppendLine("Host tools (read_file, apply_patch, run_command, vcs.*) are NOT available right now.");
        sb.AppendLine("If the user asks you to use one, explain that the assistant workspace isn't writable and ask them to confirm with the operator that AssistantWorkspaceRoot is mounted as a writable volume.");
        sb.Append("</workspace-unavailable>");
        return sb.ToString();
    }

    private static string BuildWorkspaceSwitchNotice(string? previousSignature, ResolvedWorkspace next)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<workspace-switch>");
        sb.Append("Previous workspace: ").AppendLine(previousSignature ?? "(none)");
        sb.Append("Active workspace: ").AppendLine(next.Signature);
        if (next.TraceId is { } traceId)
        {
            sb.Append("This is the workdir of trace ").Append(traceId.ToString("N")).AppendLine(" — read-only context produced by a code-aware workflow run.");
        }
        else
        {
            sb.AppendLine("This is your private per-conversation workspace; you may create, modify, and read files here.");
        }

        var entries = TryListTopLevelEntries(next.Context.RootPath);
        if (entries.Count > 0)
        {
            sb.AppendLine("Top-level entries:");
            foreach (var entry in entries)
            {
                sb.Append("- ").AppendLine(entry);
            }
        }
        else
        {
            sb.AppendLine("Workspace is currently empty.");
        }

        sb.AppendLine("Re-orient before assuming any prior file paths or repository state still applies.");
        sb.Append("</workspace-switch>");
        return sb.ToString();
    }

    private static IReadOnlyList<string> TryListTopLevelEntries(string root)
    {
        try
        {
            // Bound the listing so a wildly populated trace workdir doesn't blow the system prompt.
            const int max = 24;
            var dirs = Directory.EnumerateDirectories(root)
                .Select(p => Path.GetFileName(p) + "/")
                .Take(max);
            var files = Directory.EnumerateFiles(root)
                .Select(p => Path.GetFileName(p) ?? string.Empty)
                .Take(max);
            return dirs.Concat(files)
                .Where(name => !string.IsNullOrEmpty(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private sealed record ResolvedWorkspace(ToolWorkspaceContext Context, string Signature, Guid? TraceId);

    /// <summary>
    /// Returns a per-turn dispatcher that contains the built-in IAssistantTool registrations plus
    /// the role-granted adapters. Built-ins win on name collisions — a misconfigured role can't
    /// shadow CodeFlow domain tools (e.g. <c>list_workflows</c>).
    /// </summary>
    private static AssistantToolDispatcher MergeDispatcher(
        AssistantToolDispatcher builtIn,
        IReadOnlyList<IAssistantTool> roleTools)
    {
        var merged = new List<IAssistantTool>(builtIn.Tools);
        var existing = new HashSet<string>(merged.Select(t => t.Name), StringComparer.Ordinal);
        foreach (var tool in roleTools)
        {
            if (existing.Add(tool.Name))
            {
                merged.Add(tool);
            }
        }
        return new AssistantToolDispatcher(merged);
    }

    /// <summary>
    /// Same as <see cref="MergeDispatcher"/> but with the override priority inverted: tools in
    /// <paramref name="overrideTools"/> replace any built-in with the same name. Used by the
    /// workflow-draft factory so its workspace-aware <c>save_workflow_package</c> overrides the
    /// DI-registered workspace-blind fallback for the LLM's view.
    /// </summary>
    private static AssistantToolDispatcher MergeDispatcherWithOverride(
        AssistantToolDispatcher builtIn,
        IReadOnlyList<IAssistantTool> overrideTools)
    {
        var overrideNames = new HashSet<string>(
            overrideTools.Select(t => t.Name),
            StringComparer.Ordinal);
        var merged = new List<IAssistantTool>(
            builtIn.Tools.Where(t => !overrideNames.Contains(t.Name)));
        merged.AddRange(overrideTools);
        return new AssistantToolDispatcher(merged);
    }

    private async IAsyncEnumerable<AssistantStreamItem> AskAnthropicAsync(
        AssistantRuntimeConfig config,
        string cacheableSystemPrompt,
        string volatileSystemPrompt,
        string userMessage,
        IReadOnlyList<AssistantMessage> history,
        IReadOnlyCollection<IAssistantTool> allowedTools,
        AssistantToolDispatcher dispatcher,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var apiKey = await providerSettings.GetDecryptedApiKeyAsync(LlmProviderKeys.Anthropic, cancellationToken)
            ?? throw new InvalidOperationException("Anthropic API key is not configured.");

        var settings = await providerSettings.GetAsync(LlmProviderKeys.Anthropic, cancellationToken);
        var endpointUrl = settings?.EndpointUrl;

        var client = anthropicClient.WithOptions(opts => opts with
        {
            APIKey = apiKey,
            BaseUrl = string.IsNullOrWhiteSpace(endpointUrl) ? opts.BaseUrl : new Uri(endpointUrl)
        });

        // CE-1: mark the last tool with cache_control=ephemeral. Anthropic caches the entire
        // tools-array prefix when the marker lands on the LAST entry, so a single breakpoint is
        // enough — no need to mark every tool.
        var anthropicTools = allowedTools.Count > 0 ? AnthropicToolMapper.Map(allowedTools, markLastEphemeral: true) : null;

        var messages = new List<MessageParam>(history.Count + 1);
        foreach (var msg in history)
        {
            if (msg.Role is AssistantMessageRole.System) continue;
            messages.Add(new MessageParam
            {
                Role = msg.Role == AssistantMessageRole.Assistant ? Role.Assistant : Role.User,
                Content = msg.Content
            });
        }
        messages.Add(new MessageParam { Role = Role.User, Content = userMessage });

        // CE-3 / N=1: keep at most one full workflow-package payload in the transcript at any
        // time. The tracker remembers the current carrier (an emission OR a fetch result) and
        // demotes it to the redacted shape when a new carrier replaces it. Symmetric across
        // input-side and result-side carriers.
        var carrierTracker = new WorkflowPackageCarrierTracker();

        MessageDeltaUsage? finalUsage = null;
        for (var turn = 0; turn < config.MaxTurns; turn++)
        {
            var createParams = BuildAnthropicParams(config, cacheableSystemPrompt, volatileSystemPrompt, messages, anthropicTools);

            // Per-turn accumulators: text gets streamed back to the caller; tool_use blocks gather
            // their input JSON across multiple InputJSONDelta events keyed by content-block index.
            var assistantTextThisTurn = new StringBuilder();
            var pendingToolUses = new Dictionary<long, PendingAnthropicToolUse>();
            string? stopReason = null;

            await foreach (var ev in client.Messages.CreateStreaming(createParams, cancellationToken))
            {
                if (ev.TryPickContentBlockStart(out var cbStart))
                {
                    if (cbStart.ContentBlock.TryPickToolUse(out var toolUse))
                    {
                        pendingToolUses[cbStart.Index] = new PendingAnthropicToolUse(
                            toolUse.ID,
                            toolUse.Name,
                            new StringBuilder());
                    }
                }
                else if (ev.TryPickContentBlockDelta(out var cbDelta))
                {
                    if (cbDelta.Delta.TryPickText(out var td))
                    {
                        assistantTextThisTurn.Append(td.Text);
                        yield return new AssistantTextDelta(td.Text);
                    }
                    else if (cbDelta.Delta.TryPickInputJSON(out var ijd) &&
                             pendingToolUses.TryGetValue(cbDelta.Index, out var pending))
                    {
                        pending.JsonBuffer.Append(ijd.PartialJSON);
                    }
                }
                else if (ev.TryPickDelta(out var msgDelta))
                {
                    finalUsage = msgDelta.Usage;
                    if (msgDelta.Delta.StopReason.HasValue)
                    {
                        stopReason = msgDelta.Delta.StopReason.Value.ToString();
                    }
                }
            }

            // No tool calls — final answer in hand. Emit usage + done.
            if (pendingToolUses.Count == 0)
            {
                if (SerializeAnthropicUsage(finalUsage) is { } usageJson)
                {
                    yield return new AssistantTokenUsage(LlmProviderKeys.Anthropic, config.Model, usageJson);
                }
                yield return new AssistantTurnDone(LlmProviderKeys.Anthropic, config.Model);
                yield break;
            }

            // Persist usage from this turn before we run tools — token tracking should reflect the
            // model call regardless of how many tool round-trips follow.
            if (SerializeAnthropicUsage(finalUsage) is { } turnUsageJson)
            {
                yield return new AssistantTokenUsage(LlmProviderKeys.Anthropic, config.Model, turnUsageJson);
            }
            finalUsage = null;

            // CE-3: dispatch tools FIRST so we can decide whether to redact each tool_use's Input
            // before it lands in the in-turn assistant transcript. The streamed UI events still
            // carry the original args (the UI tool-call cards display what the model emitted);
            // only the transcript fed to the next provider call gets the redacted Input.
            //
            // Reordering is safe: Anthropic's protocol cares about the *final* messages array
            // ordering (assistant tool_use turn immediately followed by user tool_result turn),
            // and we still preserve that order when we append below.
            var dispatched = new List<(PendingAnthropicToolUse Pending, JsonElement OriginalArgs, AssistantToolResult Result)>(pendingToolUses.Count);
            foreach (var (_, pending) in pendingToolUses.OrderBy(kvp => kvp.Key))
            {
                var args = ParseToolArguments(pending.JsonBuffer.ToString());
                yield return new AssistantToolCallStarted(pending.Id, pending.Name, args);
                var result = await dispatcher.InvokeAsync(pending.Name, args, cancellationToken);
                yield return new AssistantToolCallCompleted(pending.Id, pending.Name, result.ResultJson, result.IsError);
                dispatched.Add((pending, args, result));
            }

            // Echo the assistant turn (text + tool_use blocks) back into history so the model has
            // a coherent transcript when we send tool results. Inputs are appended in their FULL
            // form here; the carrier tracker demotes any prior carrier in-place so at most one
            // full package payload lives in the transcript across the whole AskAsync invocation.
            var assistantBlocks = new List<ContentBlockParam>();
            if (assistantTextThisTurn.Length > 0)
            {
                assistantBlocks.Add((TextBlockParam)new TextBlockParam { Text = assistantTextThisTurn.ToString() });
            }
            foreach (var (pending, originalArgs, result) in dispatched)
            {
                var inputForTranscript = ParseToolInputAsDictionary(pending.JsonBuffer.ToString());
                assistantBlocks.Add((ToolUseBlockParam)new ToolUseBlockParam
                {
                    ID = pending.Id,
                    Name = pending.Name,
                    Input = inputForTranscript,
                });

                if (!result.IsError
                    && WorkflowPackageRedaction.InputCarriesPackagePayload(pending.Name, originalArgs))
                {
                    var blocksRef = assistantBlocks;
                    var blockIdx = assistantBlocks.Count - 1;
                    var pendingId = pending.Id;
                    var pendingName = pending.Name;
                    var argsClone = originalArgs.Clone();
                    carrierTracker.Replace(() =>
                    {
                        var redactedDict = WorkflowPackageRedaction.RedactArgsAsDictionary(pendingName, argsClone);
                        blocksRef[blockIdx] = (ToolUseBlockParam)new ToolUseBlockParam
                        {
                            ID = pendingId,
                            Name = pendingName,
                            Input = redactedDict,
                        };
                    });
                }
            }
            messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantBlocks });

            // tool_result blocks go in a user-role message that immediately follows the assistant
            // turn. ToolUseID has to match the assistant block's ID exactly. Result-side carriers
            // (get_workflow_package_draft, get_workflow_package) register with the tracker so
            // they participate in the same N=1 buffer as input-side carriers.
            var resultBlocks = new List<ContentBlockParam>();
            foreach (var (pending, _, result) in dispatched)
            {
                resultBlocks.Add((ToolResultBlockParam)new ToolResultBlockParam
                {
                    ToolUseID = pending.Id,
                    Content = result.ResultJson,
                    IsError = result.IsError ? true : null,
                });

                if (!result.IsError
                    && WorkflowPackageRedaction.ResultCarriesPackagePayload(pending.Name, result.ResultJson))
                {
                    var blocksRef = resultBlocks;
                    var blockIdx = resultBlocks.Count - 1;
                    var pendingId = pending.Id;
                    var pendingName = pending.Name;
                    var resultJsonOriginal = result.ResultJson;
                    carrierTracker.Replace(() =>
                    {
                        var redactedJson = WorkflowPackageRedaction.RedactResultJson(pendingName, resultJsonOriginal);
                        blocksRef[blockIdx] = (ToolResultBlockParam)new ToolResultBlockParam
                        {
                            ToolUseID = pendingId,
                            Content = redactedJson,
                            IsError = null,
                        };
                    });
                }
            }

            messages.Add(new MessageParam { Role = Role.User, Content = resultBlocks });

            // Loop continues — model gets another shot with the tool results in context.
            _ = stopReason; // captured for future logging; not used yet
        }

        logger.LogWarning(
            "Anthropic assistant turn hit MaxTurns={MaxTurns} without producing a final answer.",
            config.MaxTurns);
        yield return new AssistantTurnError(
            $"Assistant exceeded the {config.MaxTurns}-turn tool-loop budget without producing a final answer.");
    }

    private static MessageCreateParams BuildAnthropicParams(
        AssistantRuntimeConfig config,
        string cacheableSystemPrompt,
        string volatileSystemPrompt,
        List<MessageParam> messages,
        IReadOnlyList<ToolUnion>? tools)
    {
        // CE-1: build the system as a TextBlockParam list so we can mark the cacheable head with
        // cache_control=ephemeral. Anthropic's cache breakpoint applies to that block and the
        // accumulated content before it (here: the tools-array prefix and the model/max_tokens
        // metadata). The volatile tail is a second TextBlockParam with no cache marker — it lives
        // in the same `system` field so the model reads it as part of the system prompt, but it
        // sits AFTER the breakpoint so per-turn changes don't invalidate the cached prefix.
        var systemBlocks = BuildSystemBlocks(cacheableSystemPrompt, volatileSystemPrompt);
        var hasSystem = systemBlocks.Count > 0;

        // System is init-only on MessageCreateParams, so we branch on whether to set it and on
        // whether tools are present.
        if (!hasSystem)
        {
            return tools is null
                ? new MessageCreateParams { Model = config.Model, MaxTokens = config.MaxTokens, Messages = messages }
                : new MessageCreateParams
                {
                    Model = config.Model,
                    MaxTokens = config.MaxTokens,
                    Messages = messages,
                    Tools = tools.ToList()
                };
        }

        return tools is null
            ? new MessageCreateParams
            {
                Model = config.Model,
                MaxTokens = config.MaxTokens,
                System = systemBlocks,
                Messages = messages
            }
            : new MessageCreateParams
            {
                Model = config.Model,
                MaxTokens = config.MaxTokens,
                System = systemBlocks,
                Messages = messages,
                Tools = tools.ToList()
            };
    }

    /// <summary>
    /// CE-1: assemble the system prompt as a list of TextBlockParam so the cacheable head can
    /// carry a cache_control=ephemeral marker and the volatile tail can sit after the breakpoint.
    /// Empty strings collapse — if both are empty the caller skips setting System entirely.
    /// </summary>
    internal static List<TextBlockParam> BuildSystemBlocks(string cacheableSystemPrompt, string volatileSystemPrompt)
    {
        var blocks = new List<TextBlockParam>(2);
        if (!string.IsNullOrWhiteSpace(cacheableSystemPrompt))
        {
            blocks.Add(new TextBlockParam
            {
                Text = cacheableSystemPrompt,
                CacheControl = new CacheControlEphemeral()
            });
        }
        if (!string.IsNullOrWhiteSpace(volatileSystemPrompt))
        {
            blocks.Add(new TextBlockParam
            {
                Text = volatileSystemPrompt
            });
        }
        return blocks;
    }

    private async IAsyncEnumerable<AssistantStreamItem> AskOpenAiAsync(
        AssistantRuntimeConfig config,
        string cacheableSystemPrompt,
        string volatileSystemPrompt,
        string userMessage,
        IReadOnlyList<AssistantMessage> history,
        IReadOnlyCollection<IAssistantTool> allowedTools,
        AssistantToolDispatcher dispatcher,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var apiKey = await providerSettings.GetDecryptedApiKeyAsync(config.Provider, cancellationToken)
            ?? throw new InvalidOperationException($"{config.Provider} API key is not configured.");

        var settings = await providerSettings.GetAsync(config.Provider, cancellationToken);
        var endpointUrl = settings?.EndpointUrl;

        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(endpointUrl) && Uri.TryCreate(endpointUrl, UriKind.Absolute, out var endpointUri))
        {
            clientOptions.Endpoint = endpointUri;
        }

        var chatClient = new ChatClient(config.Model, new ApiKeyCredential(apiKey), clientOptions);

        var openAiTools = allowedTools.Count > 0 ? OpenAiToolMapper.Map(allowedTools) : null;

        // CE-1: send the cacheable head and the volatile tail as two separate SystemChatMessages.
        // OpenAI's automatic prompt caching keys off of the longest stable prefix, so keeping the
        // first message stable across turns lets it cache while the volatile tail still flows in.
        var chatMessages = new List<OpenAI.Chat.ChatMessage>(history.Count + 3);
        if (!string.IsNullOrWhiteSpace(cacheableSystemPrompt))
        {
            chatMessages.Add(new SystemChatMessage(cacheableSystemPrompt));
        }
        if (!string.IsNullOrWhiteSpace(volatileSystemPrompt))
        {
            chatMessages.Add(new SystemChatMessage(volatileSystemPrompt));
        }
        foreach (var msg in history)
        {
            OpenAI.Chat.ChatMessage built = msg.Role switch
            {
                AssistantMessageRole.System => new SystemChatMessage(msg.Content),
                AssistantMessageRole.Assistant => new AssistantChatMessage(msg.Content),
                _ => new UserChatMessage(msg.Content)
            };
            chatMessages.Add(built);
        }
        chatMessages.Add(new UserChatMessage(userMessage));

        // CE-3 / N=1: same carrier tracker pattern as the Anthropic path. Bounds the
        // transcript to at most one full workflow-package payload at any time.
        var carrierTracker = new WorkflowPackageCarrierTracker();

        string responseModel = config.Model;
        for (var turn = 0; turn < config.MaxTurns; turn++)
        {
            var completionOptions = new ChatCompletionOptions { MaxOutputTokenCount = config.MaxTokens };
            if (openAiTools is not null)
            {
                foreach (var tool in openAiTools) completionOptions.Tools.Add(tool);
            }

            var assistantTextThisTurn = new StringBuilder();
            var pendingToolCalls = new Dictionary<int, PendingOpenAiToolCall>();
            ChatTokenUsage? finalUsage = null;
            ChatFinishReason? finishReason = null;

            await foreach (var update in chatClient.CompleteChatStreamingAsync(chatMessages, completionOptions, cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(update.Model))
                {
                    responseModel = update.Model;
                }

                foreach (var part in update.ContentUpdate)
                {
                    if (!string.IsNullOrEmpty(part.Text))
                    {
                        assistantTextThisTurn.Append(part.Text);
                        yield return new AssistantTextDelta(part.Text);
                    }
                }

                foreach (var tcUpdate in update.ToolCallUpdates)
                {
                    if (!pendingToolCalls.TryGetValue(tcUpdate.Index, out var pending))
                    {
                        pending = new PendingOpenAiToolCall(tcUpdate.ToolCallId, tcUpdate.FunctionName, new StringBuilder());
                        pendingToolCalls[tcUpdate.Index] = pending;
                    }
                    else
                    {
                        // The id and function name only arrive on the first chunk in OpenAI's
                        // streaming protocol; later chunks carry only the arguments delta.
                        if (string.IsNullOrEmpty(pending.Id) && !string.IsNullOrEmpty(tcUpdate.ToolCallId))
                        {
                            pending = pending with { Id = tcUpdate.ToolCallId };
                            pendingToolCalls[tcUpdate.Index] = pending;
                        }
                        if (string.IsNullOrEmpty(pending.Name) && !string.IsNullOrEmpty(tcUpdate.FunctionName))
                        {
                            pending = pending with { Name = tcUpdate.FunctionName };
                            pendingToolCalls[tcUpdate.Index] = pending;
                        }
                    }

                    if (tcUpdate.FunctionArgumentsUpdate is { } argsUpdate)
                    {
                        pending.JsonBuffer.Append(argsUpdate.ToString());
                    }
                }

                if (update.FinishReason.HasValue)
                {
                    finishReason = update.FinishReason;
                }

                if (update.Usage is not null)
                {
                    finalUsage = update.Usage;
                }
            }

            if (pendingToolCalls.Count == 0)
            {
                if (SerializeOpenAiUsage(finalUsage) is { } usageJson)
                {
                    yield return new AssistantTokenUsage(config.Provider, responseModel, usageJson);
                }
                yield return new AssistantTurnDone(config.Provider, responseModel);
                yield break;
            }

            if (SerializeOpenAiUsage(finalUsage) is { } turnUsageJson)
            {
                yield return new AssistantTokenUsage(config.Provider, responseModel, turnUsageJson);
            }

            // CE-3: dispatch tools FIRST so we can redact giant args before they land in the
            // assistant turn that's about to be appended. The streamed UI events carry the
            // original args; only the transcript bytes that re-flow into the next OpenAI call
            // get the redacted form.
            var dispatchedOpenAi = new List<(PendingOpenAiToolCall Pending, JsonElement OriginalArgs, AssistantToolResult Result)>(pendingToolCalls.Count);
            foreach (var (_, pending) in pendingToolCalls.OrderBy(kvp => kvp.Key))
            {
                var args = ParseToolArguments(pending.JsonBuffer.ToString());
                var id = pending.Id ?? string.Empty;
                var name = pending.Name ?? string.Empty;
                yield return new AssistantToolCallStarted(id, name, args);
                var result = await dispatcher.InvokeAsync(name, args, cancellationToken);
                yield return new AssistantToolCallCompleted(id, name, result.ResultJson, result.IsError);
                dispatchedOpenAi.Add((pending, args, result));
            }

            // Build a single AssistantChatMessage that carries (a) any text the model emitted
            // before the tool calls and (b) the tool_calls themselves with FULL args. The
            // carrier tracker demotes any prior input-carrier in-place so at most one full
            // payload sits in the OpenAI transcript.
            var toolCallList = dispatchedOpenAi
                .Select(d =>
                {
                    var p = d.Pending;
                    var name = p.Name ?? throw new InvalidOperationException("OpenAI tool call missing function name.");
                    return ChatToolCall.CreateFunctionToolCall(
                        p.Id ?? throw new InvalidOperationException("OpenAI tool call missing id."),
                        name,
                        BinaryData.FromBytes(Encoding.UTF8.GetBytes(p.JsonBuffer.ToString())));
                })
                .ToList();

            var assistantText = assistantTextThisTurn.Length > 0 ? assistantTextThisTurn.ToString() : null;
            var assistantTurn = new AssistantChatMessage(toolCallList);
            if (assistantText is not null)
            {
                assistantTurn.Content.Add(ChatMessageContentPart.CreateTextPart(assistantText));
            }
            chatMessages.Add(assistantTurn);
            var assistantTurnIdx = chatMessages.Count - 1;

            for (var i = 0; i < dispatchedOpenAi.Count; i++)
            {
                var d = dispatchedOpenAi[i];
                var name = d.Pending.Name ?? string.Empty;
                if (d.Result.IsError) continue;
                if (!WorkflowPackageRedaction.InputCarriesPackagePayload(name, d.OriginalArgs)) continue;

                var idx = i;
                var pendingId = d.Pending.Id ?? string.Empty;
                var argsClone = d.OriginalArgs.Clone();
                carrierTracker.Replace(() =>
                {
                    var redactedJson = WorkflowPackageRedaction.RedactArgsAsJsonString(name, argsClone);
                    toolCallList[idx] = ChatToolCall.CreateFunctionToolCall(
                        pendingId, name, BinaryData.FromBytes(Encoding.UTF8.GetBytes(redactedJson)));
                    var rebuilt = new AssistantChatMessage(toolCallList);
                    if (assistantText is not null)
                    {
                        rebuilt.Content.Add(ChatMessageContentPart.CreateTextPart(assistantText));
                    }
                    chatMessages[assistantTurnIdx] = rebuilt;
                });
            }

            // tool result messages reference the original tool_call id. Result-side carriers
            // register so they participate in the same N=1 buffer.
            foreach (var d in dispatchedOpenAi)
            {
                var name = d.Pending.Name ?? string.Empty;
                var pendingId = d.Pending.Id ?? string.Empty;
                chatMessages.Add(new ToolChatMessage(pendingId, d.Result.ResultJson));
                var resultMsgIdx = chatMessages.Count - 1;

                if (d.Result.IsError) continue;
                if (!WorkflowPackageRedaction.ResultCarriesPackagePayload(name, d.Result.ResultJson)) continue;

                var resultJsonOriginal = d.Result.ResultJson;
                carrierTracker.Replace(() =>
                {
                    var redactedJson = WorkflowPackageRedaction.RedactResultJson(name, resultJsonOriginal);
                    chatMessages[resultMsgIdx] = new ToolChatMessage(pendingId, redactedJson);
                });
            }

            _ = finishReason; // available for future logging
        }

        logger.LogWarning(
            "OpenAI assistant turn hit MaxTurns={MaxTurns} without producing a final answer.",
            config.MaxTurns);
        yield return new AssistantTurnError(
            $"Assistant exceeded the {config.MaxTurns}-turn tool-loop budget without producing a final answer.");
    }

    private static Dictionary<string, JsonElement> ParseToolInputAsDictionary(string json)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return dict;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return dict;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.Clone();
            }
        }
        catch (JsonException)
        {
            // Leave empty — the dispatcher will surface a tool error to the model.
        }
        return dict;
    }

    private static JsonElement ParseToolArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }

    private sealed record PendingAnthropicToolUse(string Id, string Name, StringBuilder JsonBuffer);
    private sealed record PendingOpenAiToolCall(string? Id, string? Name, StringBuilder JsonBuffer);

    private static JsonElement? SerializeAnthropicUsage(MessageDeltaUsage? final)
    {
        if (final is null)
        {
            return null;
        }

        // Verbatim passthrough — TokenUsageRecord stores raw provider fields so future provider
        // additions (cache_creation_input_tokens, etc.) land without a code change here.
        var combined = new Dictionary<string, object?>
        {
            ["input_tokens"] = final.InputTokens,
            ["output_tokens"] = final.OutputTokens,
            ["cache_creation_input_tokens"] = final.CacheCreationInputTokens,
            ["cache_read_input_tokens"] = final.CacheReadInputTokens,
        };

        return JsonSerializer.SerializeToElement(combined);
    }

    internal static JsonElement? SerializeOpenAiUsage(OpenAI.Chat.ChatTokenUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        // CE-2: include the breakdown sub-objects so the aggregator can flatten them onto dotted
        // paths (e.g. `prompt_tokens_details.cached_tokens`) that match OpenAI's wire shape. The
        // wire shape uses `_details` suffixes so we mirror it here. Fields are emitted even when
        // zero so dashboards can distinguish "no cache" from "field missing".
        var combined = new Dictionary<string, object?>
        {
            ["prompt_tokens"] = usage.InputTokenCount,
            ["completion_tokens"] = usage.OutputTokenCount,
            ["total_tokens"] = usage.TotalTokenCount,
            ["prompt_tokens_details"] = new Dictionary<string, object?>
            {
                ["cached_tokens"] = usage.InputTokenDetails?.CachedTokenCount ?? 0,
                ["audio_tokens"] = usage.InputTokenDetails?.AudioTokenCount ?? 0,
            },
            ["completion_tokens_details"] = new Dictionary<string, object?>
            {
                ["reasoning_tokens"] = usage.OutputTokenDetails?.ReasoningTokenCount ?? 0,
                ["audio_tokens"] = usage.OutputTokenDetails?.AudioTokenCount ?? 0,
                // Predicted-output fields (accepted/rejected_prediction_tokens) are gated behind
                // the SDK's OPENAI001 evaluation diagnostic in 2.10.0. Skip them until the API
                // stabilizes — `cached_tokens` and `reasoning_tokens` are the load-bearing fields
                // for the assistant cost-observability story.
            },
        };

        return JsonSerializer.SerializeToElement(combined);
    }
}
