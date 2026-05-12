using CodeFlow.Contracts;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;
using Scriban.Syntax;
using System.Text.Json;

namespace CodeFlow.Orchestration;

/// <summary>
/// ForEach-node dispatch + completion helpers (sc-942 / sc-943). Models on the Swarm partial for
/// per-node-kind isolation: the dispatcher publishes the first iteration via
/// <see cref="PublishForEachFirstDispatchAsync"/>; the parent saga's <c>RouteSubflowCompletionAsync</c>
/// calls <see cref="HandleForEachIterationCompletionAsync"/> once per child completion to either
/// (a) publish the next iteration, (b) abort with Failed on a child failure, or (c) write the
/// aggregate output artifact and route through Continue.
/// </summary>
public sealed partial class WorkflowSagaStateMachine
{
    /// <summary>Output port the saga routes through when the aggregated iteration completes.</summary>
    internal const string ForEachContinuePort = "Continue";

    /// <summary>
    /// Workflow-context bag key holding the snapshotted iteration items for a given ForEach node.
    /// Underscore + dot prefix marks the key as runtime-internal — author prompts must not read or
    /// write it. The node id is appended so nested ForEach nodes don't collide.
    /// </summary>
    internal static string ForEachItemsBagKey(Guid nodeId) => $"__codeflow.foreach.{nodeId:N}.items";

    /// <summary>
    /// First-dispatch entry for a ForEach node. Evaluates the configured Scriban expression against
    /// the saga's workflow context, snapshots the resulting JSON array onto the workflow bag plus
    /// the iteration tracking columns, and either (a) short-circuits through Continue with an empty
    /// aggregate when the collection is empty/null, or (b) spawns the first iteration's child saga.
    /// </summary>
    internal static async Task PublishForEachFirstDispatchAsync(
        BehaviorContext<WorkflowSagaStateEntity> context,
        Scripting.LogicNodeScriptHost scriptHost,
        IArtifactStore artifactStore,
        WorkflowSagaStateEntity saga,
        Workflow workflow,
        WorkflowNode forEachNode,
        Uri inputRef,
        Guid roundId)
    {
        if (string.IsNullOrWhiteSpace(forEachNode.CollectionExpression))
        {
            throw new InvalidOperationException(
                $"ForEach node {forEachNode.Id} in workflow {workflow.Key} v{workflow.Version} "
                + "has no CollectionExpression configured. Validator should have rejected this on save.");
        }

        if (string.IsNullOrWhiteSpace(forEachNode.SubflowKey))
        {
            throw new InvalidOperationException(
                $"ForEach node {forEachNode.Id} in workflow {workflow.Key} v{workflow.Version} "
                + "has no SubflowKey configured. Validator should have rejected this on save.");
        }

        var workflowBag = DeserializeContextInputs(saga.WorkflowInputsJson);
        var contextBag = DeserializeContextInputs(saga.InputsJson);

        JsonElement[] items;
        try
        {
            items = EvaluateCollectionExpression(forEachNode.CollectionExpression, contextBag, workflowBag);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            saga.PendingTransition = PendingTransitionFailed;
            saga.FailureReason =
                $"ForEach node {forEachNode.Id}: failed to evaluate collectionExpression '{forEachNode.CollectionExpression}': {ex.Message}";
            saga.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        saga.CurrentInputRef = inputRef.ToString();
        saga.CurrentNodeId = forEachNode.Id;
        saga.CurrentRoundId = roundId;
        saga.CurrentRoundEnteredAtUtc = DateTime.UtcNow;
        saga.ForEachTotalItems = items.Length;
        saga.ForEachItemOutputsJson = "[]";

        if (items.Length == 0)
        {
            // Empty collection: short-circuit to Continue with an empty aggregate output. The saga's
            // route-after-decision path treats this like any other terminal decision on the ForEach
            // node, so the edge from ForEach.Continue → next-node fires normally.
            saga.CurrentForEachIndex = null;
            saga.ForEachTotalItems = 0;
            await CompleteForEachAsync(context, artifactStore, saga, workflow, forEachNode, inputRef);
            return;
        }

        // Snapshot the items on the workflow bag under a private key so the saga can replay them
        // across iterations without re-evaluating the expression (which could drift if a child
        // mutates workflow state mid-loop). The bag is the saga's persistent shared store, so the
        // items survive the per-iteration child saga's setWorkflow writes that flow back on
        // SubflowCompleted (the merge is last-write-wins per top-level key — our private key never
        // collides with author-defined keys).
        var bag = new Dictionary<string, JsonElement>(workflowBag, StringComparer.Ordinal)
        {
            [ForEachItemsBagKey(forEachNode.Id)] = SerializeJsonArrayElement(items),
        };
        saga.WorkflowInputsJson = SerializeContextInputs(bag);

        saga.CurrentForEachIndex = 0;
        saga.UpdatedAtUtc = DateTime.UtcNow;

        await PublishSubflowDispatchAsync(
            context,
            scriptHost,
            artifactStore,
            saga,
            workflow,
            forEachNode,
            inputRef: inputRef,
            roundId: roundId,
            loopContext: new ForEachInvocationContext(
                ItemJson: items[0].GetRawText(),
                Index: 0,
                Count: items.Length));
    }

    /// <summary>
    /// Per-iteration completion handler invoked from <c>RouteSubflowCompletionAsync</c> when the
    /// parent node is a ForEach. Returns true when the saga has dispatched the next iteration (or
    /// the aggregate Continue / Failed route) — the caller should NOT fall through to the standard
    /// subflow-decision routing path. Returns false to signal an unexpected state the caller should
    /// surface as a generic failure.
    /// </summary>
    internal static async Task<bool> HandleForEachIterationCompletionAsync(
        BehaviorContext<WorkflowSagaStateEntity, SubflowCompleted> context,
        Scripting.LogicNodeScriptHost scriptHost,
        IArtifactStore artifactStore,
        WorkflowSagaStateEntity saga,
        Workflow workflow,
        WorkflowNode forEachNode,
        string childEffectivePort,
        Uri childOutputRef)
    {
        if (saga.CurrentForEachIndex is not int currentIndex
            || saga.ForEachTotalItems is not int total)
        {
            // Defensive: saga.CurrentForEachIndex should always be set while inside a ForEach loop.
            // If the iteration state is missing the saga can't recover; surface as a Failed decision.
            saga.PendingTransition = PendingTransitionFailed;
            saga.FailureReason =
                $"ForEach node {forEachNode.Id}: iteration completion arrived but the saga has no "
                + $"ForEach state (CurrentForEachIndex={saga.CurrentForEachIndex}, ForEachTotalItems={saga.ForEachTotalItems}).";
            saga.UpdatedAtUtc = DateTime.UtcNow;
            return false;
        }

        // First-failure-aborts: a child terminating on its Failed port short-circuits the iteration.
        // We persist the failing index in failure_reason so operators can see which item caused the
        // saga to fail without scanning the trace timeline.
        if (string.Equals(childEffectivePort, ImplicitFailedPort, StringComparison.Ordinal))
        {
            ClearForEachState(saga, forEachNode.Id);
            saga.PendingTransition = PendingTransitionFailed;
            saga.FailureReason =
                $"ForEach node {forEachNode.Id}: iteration {currentIndex + 1}/{total} failed.";
            saga.UpdatedAtUtc = DateTime.UtcNow;
            return true;
        }

        // Append the child's output reference into the outputs array.
        var outputs = ReadForEachOutputs(saga);
        outputs.Add(JsonSerializer.SerializeToElement(new
        {
            index = currentIndex,
            outputRef = childOutputRef.ToString(),
            port = childEffectivePort,
        }));
        saga.ForEachItemOutputsJson = SerializeJsonArrayString(outputs);

        // Append a decision row for the iteration so the trace inspector renders one entry per
        // item, attributed to the ForEach node.
        saga.AppendDecision(new DecisionRecord(
            AgentKey: BuildSubflowSyntheticAgentKey(forEachNode),
            AgentVersion: 0,
            Decision: childEffectivePort,
            DecisionPayload: null,
            RoundId: saga.CurrentRoundId,
            RecordedAtUtc: DateTime.UtcNow,
            NodeId: forEachNode.Id,
            OutputPortName: childEffectivePort,
            InputRef: saga.CurrentInputRef,
            OutputRef: childOutputRef.ToString(),
            NodeEnteredAtUtc: saga.CurrentRoundEnteredAtUtc));

        var nextIndex = currentIndex + 1;
        if (nextIndex >= total)
        {
            // Final iteration done — write the aggregate output artifact and route Continue.
            if (saga.CurrentInputRef is null
                || !Uri.TryCreate(saga.CurrentInputRef, UriKind.Absolute, out var aggregateInputRef))
            {
                saga.PendingTransition = PendingTransitionFailed;
                saga.FailureReason =
                    $"ForEach node {forEachNode.Id}: missing input ref at aggregate completion.";
                saga.UpdatedAtUtc = DateTime.UtcNow;
                return true;
            }

            await CompleteForEachAsync(context, artifactStore, saga, workflow, forEachNode, aggregateInputRef);
            return true;
        }

        // Spawn the next iteration. Reuse the original input ref — each child sees the same node
        // input artifact; per-iteration differentiation flows through the loop context.
        if (saga.CurrentInputRef is null
            || !Uri.TryCreate(saga.CurrentInputRef, UriKind.Absolute, out var nextInputRef))
        {
            saga.PendingTransition = PendingTransitionFailed;
            saga.FailureReason =
                $"ForEach node {forEachNode.Id}: missing input ref between iterations {currentIndex} → {nextIndex}.";
            saga.UpdatedAtUtc = DateTime.UtcNow;
            return true;
        }

        var items = ReadForEachItems(saga, forEachNode.Id);
        if (nextIndex >= items.Count)
        {
            saga.PendingTransition = PendingTransitionFailed;
            saga.FailureReason =
                $"ForEach node {forEachNode.Id}: snapshot items lost between iterations (have {items.Count}, need index {nextIndex}).";
            saga.UpdatedAtUtc = DateTime.UtcNow;
            return true;
        }

        saga.CurrentForEachIndex = nextIndex;
        var nextRoundId = Guid.NewGuid();
        saga.CurrentRoundId = nextRoundId;
        saga.CurrentRoundEnteredAtUtc = DateTime.UtcNow;
        saga.UpdatedAtUtc = saga.CurrentRoundEnteredAtUtc;

        await PublishSubflowDispatchAsync(
            context,
            scriptHost,
            artifactStore,
            saga,
            workflow,
            forEachNode,
            inputRef: nextInputRef,
            roundId: nextRoundId,
            // Skip the boundary input script on follow-up iterations — same posture ReviewLoop takes
            // on its iterate path; the script is intended to shape the artifact entering the loop
            // once, not on every iteration.
            runInputScript: false,
            loopContext: new ForEachInvocationContext(
                ItemJson: items[nextIndex].GetRawText(),
                Index: nextIndex,
                Count: total));
        return true;
    }

    private static async Task CompleteForEachAsync(
        BehaviorContext<WorkflowSagaStateEntity> context,
        IArtifactStore artifactStore,
        WorkflowSagaStateEntity saga,
        Workflow workflow,
        WorkflowNode forEachNode,
        Uri originalInputRef)
    {
        var aggregateText = saga.ForEachItemOutputsJson ?? "[]";
        var artifactId = Guid.NewGuid();
        var metadata = new ArtifactMetadata(
            TraceId: saga.TraceId,
            RoundId: saga.CurrentRoundId,
            ArtifactId: artifactId,
            ContentType: "application/json",
            FileName: $"foreach-{forEachNode.Id:N}-aggregate.json");

        Uri aggregateOutputRef;
        await using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(aggregateText)))
        {
            aggregateOutputRef = await artifactStore.WriteAsync(stream, metadata, context.CancellationToken);
        }

        // Append the terminal decision row for the ForEach node so the trace inspector shows the
        // aggregate exit alongside the per-iteration rows.
        saga.AppendDecision(new DecisionRecord(
            AgentKey: BuildSubflowSyntheticAgentKey(forEachNode),
            AgentVersion: 0,
            Decision: ForEachContinuePort,
            DecisionPayload: null,
            RoundId: saga.CurrentRoundId,
            RecordedAtUtc: DateTime.UtcNow,
            NodeId: forEachNode.Id,
            OutputPortName: ForEachContinuePort,
            InputRef: originalInputRef.ToString(),
            OutputRef: aggregateOutputRef.ToString(),
            NodeEnteredAtUtc: saga.CurrentRoundEnteredAtUtc));

        saga.LastEffectivePort = ForEachContinuePort;
        ClearForEachState(saga, forEachNode.Id);
        saga.UpdatedAtUtc = DateTime.UtcNow;

        await RouteAfterDecisionAsync(
            context,
            workflow,
            activity: null,
            sourceNodeId: forEachNode.Id,
            sourceKindLabel: "ForEach node",
            effectivePortName: ForEachContinuePort,
            effectiveOutputRef: aggregateOutputRef,
            retryContextForHandoff: null);
    }

    private static void ClearForEachState(WorkflowSagaStateEntity saga, Guid nodeId)
    {
        saga.CurrentForEachIndex = null;
        saga.ForEachTotalItems = null;
        saga.ForEachItemOutputsJson = null;

        var bag = new Dictionary<string, JsonElement>(
            DeserializeContextInputs(saga.WorkflowInputsJson),
            StringComparer.Ordinal);
        if (bag.Remove(ForEachItemsBagKey(nodeId)))
        {
            saga.WorkflowInputsJson = SerializeContextInputs(bag);
        }
    }

    private static List<JsonElement> ReadForEachItems(WorkflowSagaStateEntity saga, Guid nodeId)
    {
        var bag = DeserializeContextInputs(saga.WorkflowInputsJson);
        if (!bag.TryGetValue(ForEachItemsBagKey(nodeId), out var element)
            || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<JsonElement>();
        foreach (var item in element.EnumerateArray())
        {
            list.Add(item.Clone());
        }
        return list;
    }

    private static List<JsonElement> ReadForEachOutputs(WorkflowSagaStateEntity saga)
    {
        var raw = saga.ForEachItemOutputsJson;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var list = new List<JsonElement>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                list.Add(item.Clone());
            }
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string SerializeJsonArrayString(IReadOnlyList<JsonElement> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var entry in entries)
            {
                entry.WriteTo(writer);
            }
            writer.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static JsonElement SerializeJsonArrayElement(IReadOnlyList<JsonElement> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var entry in entries)
            {
                entry.WriteTo(writer);
            }
            writer.WriteEndArray();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// Evaluates the ForEach <c>collectionExpression</c> as a Scriban expression against the
    /// saga's workflow + context bags and returns the items as a JSON array. Throws on parse error,
    /// runtime error, or non-array/non-null result so the caller can surface a Failed decision.
    /// </summary>
    private static JsonElement[] EvaluateCollectionExpression(
        string expression,
        IReadOnlyDictionary<string, JsonElement> contextBag,
        IReadOnlyDictionary<string, JsonElement> workflowBag)
    {
        // Wrap the expression in an assignment statement so the underlying ScriptObject captures
        // the raw evaluation result (object / list / scalar) instead of stringifying it. We then
        // pull the value back out by name and JSON-serialize it. This avoids relying on a specific
        // Scriban built-in (`object.to_json`) that may not be present in every Scriban version.
        const string sentinelName = "__codeflow_foreach_result";
        var template = Template.Parse("{{ " + sentinelName + " = (" + expression + ") }}");
        if (template.HasErrors)
        {
            var details = string.Join(
                "; ",
                template.Messages.Where(m => m.Type == ParserMessageType.Error).Select(m => m.Message));
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(details)
                    ? "Scriban parse error."
                    : $"Scriban parse error: {details}.");
        }

        var globals = new ScriptObject
        {
            ["context"] = BuildScriptNamespace(contextBag),
            ["workflow"] = BuildScriptNamespace(workflowBag),
        };

        var tContext = new TemplateContext
        {
            StrictVariables = false,
            EnableRelaxedMemberAccess = true,
        };
        tContext.PushGlobal(globals);

        try
        {
            template.Render(tContext);
        }
        catch (ScriptRuntimeException ex)
        {
            throw new InvalidOperationException($"Scriban runtime error: {ex.OriginalMessage}", ex);
        }

        if (!globals.TryGetValue(sentinelName, out var raw))
        {
            return Array.Empty<JsonElement>();
        }

        return ConvertScriptResultToJsonArray(raw);
    }

    private static ScriptObject BuildScriptNamespace(IReadOnlyDictionary<string, JsonElement> entries)
    {
        var scope = new ScriptObject();
        foreach (var (key, element) in entries)
        {
            scope[key] = ConvertJsonElementToScriptValue(element);
        }
        return scope;
    }

    private static object? ConvertJsonElementToScriptValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => ConvertJsonObjectToScript(element),
            JsonValueKind.Array => ConvertJsonArrayToScript(element),
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.TryGetInt64(out var i) ? i : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => element.GetRawText(),
        };

    private static ScriptObject ConvertJsonObjectToScript(JsonElement element)
    {
        var result = new ScriptObject();
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = ConvertJsonElementToScriptValue(property.Value);
        }
        return result;
    }

    private static ScriptArray ConvertJsonArrayToScript(JsonElement element)
    {
        var result = new ScriptArray();
        foreach (var item in element.EnumerateArray())
        {
            result.Add(ConvertJsonElementToScriptValue(item));
        }
        return result;
    }

    /// <summary>
    /// Coerces a Scriban evaluation result into a JSON array of items. Null / empty collections
    /// return an empty array. Non-iterable scalars (string / number / bool) throw — the validator
    /// will reject ForEach nodes whose expression evaluates to a non-iterable, but the runtime
    /// guards too so a workflow that drifts past validation surfaces a clean failure reason.
    /// </summary>
    private static JsonElement[] ConvertScriptResultToJsonArray(object? raw)
    {
        if (raw is null)
        {
            return Array.Empty<JsonElement>();
        }

        switch (raw)
        {
            case ScriptArray array:
                return array.Select(SerializeScriptValueToJson).ToArray();
            case IEnumerable<object?> enumerable when raw is not string:
                return enumerable.Select(SerializeScriptValueToJson).ToArray();
            default:
                throw new InvalidOperationException(
                    $"expression evaluated to non-iterable value of type {raw.GetType().Name}.");
        }
    }

    private static JsonElement SerializeScriptValueToJson(object? value)
    {
        switch (value)
        {
            case null:
                return JsonSerializer.SerializeToElement<object?>(null);
            case string s:
                return JsonSerializer.SerializeToElement(s);
            case bool b:
                return JsonSerializer.SerializeToElement(b);
            case long l:
                return JsonSerializer.SerializeToElement(l);
            case int i:
                return JsonSerializer.SerializeToElement(i);
            case double d:
                return JsonSerializer.SerializeToElement(d);
            case ScriptObject obj:
                return JsonSerializer.SerializeToElement(ConvertScriptObjectToDictionary(obj));
            case ScriptArray arr:
                return JsonSerializer.SerializeToElement(arr.Select(SerializeScriptValueToJson).ToArray());
            default:
                return JsonSerializer.SerializeToElement(value.ToString());
        }
    }

    private static Dictionary<string, object?> ConvertScriptObjectToDictionary(ScriptObject obj)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in obj)
        {
            dict[pair.Key] = pair.Value switch
            {
                ScriptObject so => ConvertScriptObjectToDictionary(so),
                ScriptArray sa => sa.Select(SerializeScriptValueToJson).ToArray(),
                _ => pair.Value,
            };
        }
        return dict;
    }
}
