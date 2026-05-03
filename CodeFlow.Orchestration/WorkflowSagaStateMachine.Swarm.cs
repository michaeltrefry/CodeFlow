using CodeFlow.Contracts;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace CodeFlow.Orchestration;

/// <summary>
/// Swarm-node dispatch helpers. sc-43 shipped Sequential; sc-46 added Coordinator on top of the
/// same scaffolding (parallel dispatch, stale-round guard extension, position-tracking via the
/// pending-parallel set, Coordinator-specific token-budget cases). Lives in its own partial-class
/// file so the swarm flow doesn't bury the existing single-step routing in
/// <c>WorkflowSagaStateMachine.cs</c>.
/// </summary>
public sealed partial class WorkflowSagaStateMachine
{
    /// <summary>Allowed protocol values mirrored from the validator. Closed enum at runtime.</summary>
    internal const string SwarmProtocolSequential = "Sequential";
    internal const string SwarmProtocolCoordinator = "Coordinator";

    /// <summary>Workflow-context key holding the Swarm node's input artifact text. Cleared on
    /// node exit so a downstream swarm doesn't see a stale mission. Matches the design doc's
    /// <c>workflow.swarmMission</c>.</summary>
    internal const string WorkflowVarSwarmMission = "swarmMission";

    /// <summary>Workflow-context key holding the array of accumulated contributions. Cleared on
    /// node exit. Matches the design doc's <c>workflow.swarmContributions</c>.</summary>
    internal const string WorkflowVarSwarmContributions = "swarmContributions";

    /// <summary>Reserved workflow-context key tracking the swarm's cumulative input + output
    /// token total (across coordinator + contributors + synthesizer for this node only). Set on
    /// swarm entry, incremented on every internal completion, cleared on swarm exit. Underscore
    /// prefix marks it internal — author prompts must not read or write it.</summary>
    internal const string WorkflowVarSwarmTokensUsed = "__codeflowSwarmTokensUsed";

    /// <summary>
    /// Initial swarm dispatch: read the mission, seed the workflow context, dispatch contributor
    /// position 1. Sequential and Coordinator share the same entry surface — the configured
    /// protocol decides what happens after the first dispatch (contributor sequence vs.
    /// coordinator + parallel workers + synthesizer).
    /// </summary>
    private static async Task PublishSwarmEntryAsync(
        BehaviorContext<WorkflowSagaStateEntity> context,
        IAgentConfigRepository agentConfigRepo,
        IArtifactStore artifactStore,
        WorkflowSagaStateEntity saga,
        Workflow workflow,
        WorkflowNode swarmNode,
        Uri inputRef,
        Guid roundId)
    {
        var protocol = (swarmNode.SwarmProtocol ?? string.Empty).Trim();
        if (!string.Equals(protocol, SwarmProtocolSequential, StringComparison.Ordinal)
            && !string.Equals(protocol, SwarmProtocolCoordinator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Swarm node {swarmNode.Id} in workflow {workflow.Key} v{workflow.Version} has "
                + $"unknown protocol '{protocol}'. Validator should have rejected this on save.");
        }

        if (swarmNode.SwarmN is not int n || n < 1)
        {
            throw new InvalidOperationException(
                $"Swarm node {swarmNode.Id} has no valid n. Validator should have rejected this on save.");
        }

        if (string.IsNullOrWhiteSpace(swarmNode.ContributorAgentKey)
            || swarmNode.ContributorAgentVersion is not int)
        {
            throw new InvalidOperationException(
                $"Swarm node {swarmNode.Id} is missing pinned contributor agent. Validator should have rejected this on save.");
        }

        var isCoordinator = string.Equals(protocol, SwarmProtocolCoordinator, StringComparison.Ordinal);
        if (isCoordinator
            && (string.IsNullOrWhiteSpace(swarmNode.CoordinatorAgentKey)
                || swarmNode.CoordinatorAgentVersion is not int))
        {
            throw new InvalidOperationException(
                $"Swarm node {swarmNode.Id} (Coordinator) is missing pinned coordinator agent. Validator should have rejected this on save.");
        }

        var missionText = await ReadArtifactAsTextAsync(artifactStore, inputRef, context.CancellationToken);

        SeedSwarmWorkflowContext(saga, missionText);
        saga.CurrentSwarmNodeId = swarmNode.Id;
        saga.CurrentInputRef = inputRef.ToString();

        if (isCoordinator)
        {
            await PublishSwarmCoordinatorAsync(
                context,
                agentConfigRepo,
                saga,
                workflow,
                swarmNode,
                inputRef,
                roundId,
                n: n);
            return;
        }

        await PublishSwarmContributorAsync(
            context,
            agentConfigRepo,
            saga,
            workflow,
            swarmNode,
            inputRef,
            roundId,
            position: 1,
            n: n);
    }

    /// <summary>
    /// Returns true iff <paramref name="message"/> is a contributor completion for the given
    /// Swarm node — i.e. the agent key matches the configured contributor. In Sequential mode
    /// every contributor flows through the position-by-position dispatch path; in Coordinator
    /// mode the same agent key powers the parallel workers, distinguished by the round being
    /// in the saga's <c>PendingParallelRoundIdsJson</c> set (see
    /// <see cref="IsSwarmWorkerCompletion"/>).
    /// </summary>
    private static bool IsSwarmContributorCompletion(
        WorkflowNode swarmNode,
        AgentInvocationCompleted message)
    {
        if (string.IsNullOrWhiteSpace(swarmNode.ContributorAgentKey))
        {
            return false;
        }

        return string.Equals(message.AgentKey, swarmNode.ContributorAgentKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns true iff <paramref name="message"/> is the coordinator agent's completion for the
    /// given Coordinator-protocol Swarm node. Sequential nodes don't have a coordinator agent
    /// configured, so this always returns false in that mode.
    /// </summary>
    private static bool IsSwarmCoordinatorCompletion(
        WorkflowNode swarmNode,
        AgentInvocationCompleted message)
    {
        if (string.IsNullOrWhiteSpace(swarmNode.CoordinatorAgentKey))
        {
            return false;
        }

        return string.Equals(message.AgentKey, swarmNode.CoordinatorAgentKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns true iff the saga is currently inside a Coordinator-protocol parallel dispatch — i.e.
    /// <see cref="WorkflowSagaStateEntity.PendingParallelRoundIdsJson"/> holds a non-empty array of
    /// pending round IDs the saga is awaiting. Worker completions route through
    /// <see cref="HandleSwarmWorkerCompletionAsync"/>, contributor completions route through
    /// <see cref="HandleSwarmContributorCompletionAsync"/>, and the runtime distinguishes them by
    /// this flag.
    /// </summary>
    internal static bool IsCoordinatorParallelDispatchActive(WorkflowSagaStateEntity saga)
    {
        if (string.IsNullOrEmpty(saga.PendingParallelRoundIdsJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(saga.PendingParallelRoundIdsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                && doc.RootElement.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Stale-round guard extension for the Coordinator protocol: returns true iff the message's
    /// round id appears in the saga's <see cref="WorkflowSagaStateEntity.PendingParallelRoundIdsJson"/>
    /// pending set, AND the message's source node matches the saga's current swarm node. Used by
    /// the saga's <c>RouteCompletionAsync</c> to accept out-of-order parallel-worker completions
    /// without removing the per-message <c>RoundId == CurrentRoundId</c> check that protects
    /// every other dispatch path from delayed redeliveries.
    /// </summary>
    internal static bool IsAcceptablePendingParallelRound(
        WorkflowSagaStateEntity saga,
        AgentInvocationCompleted message)
    {
        if (saga.CurrentSwarmNodeId is not Guid swarmNodeId
            || swarmNodeId != message.FromNodeId)
        {
            return false;
        }

        return TryFindPendingParallelEntry(saga, message.RoundId, out _);
    }

    /// <summary>
    /// Appends a contributor completion to <c>workflow.swarmContributions</c>, advances the
    /// swarm position, and dispatches either the next contributor or the synthesizer. Returns
    /// non-null when the saga should terminate Failed (e.g. an unrecoverable read error). On
    /// happy paths returns null — saga state is mutated in place and a new dispatch is published.
    /// </summary>
    private static async Task<string?> HandleSwarmContributorCompletionAsync(
        BehaviorContext<WorkflowSagaStateEntity> context,
        IAgentConfigRepository agentConfigRepo,
        IArtifactStore artifactStore,
        WorkflowSagaStateEntity saga,
        Workflow workflow,
        WorkflowNode swarmNode,
        AgentInvocationCompleted message)
    {
        if (swarmNode.SwarmN is not int n || n < 1)
        {
            return $"Swarm node {swarmNode.Id} has no valid n at runtime — internal error.";
        }

        string artifactText;
        try
        {
            artifactText = message.OutputRef is null
                ? string.Empty
                : await ReadArtifactAsTextAsync(artifactStore, message.OutputRef, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Swarm node {swarmNode.Id}: failed to read contributor #"
                + $"{CountSwarmContributions(saga) + 1} output artifact: {ex.Message}";
        }

        var (role, abstained) = ParseRoleLine(artifactText);

        var workflowBag = new Dictionary<string, JsonElement>(
            DeserializeContextInputs(saga.WorkflowInputsJson),
            StringComparer.Ordinal);

        var contributions = ReadSwarmContributionsArray(workflowBag);
        var position = contributions.Count + 1;

        var contributionEntry = BuildContributionEntry(
            position: position,
            role: role,
            abstained: abstained,
            text: artifactText,
            agentKey: message.AgentKey,
            agentVersion: message.AgentVersion);

        contributions.Add(contributionEntry);
        workflowBag[WorkflowVarSwarmContributions] = SerializeJsonArray(contributions);

        // Token-budget tracking: accumulate input + output tokens for every internal LLM call.
        var tokensUsed = ReadSwarmTokensUsed(workflowBag);
        tokensUsed += message.TokenUsage.InputTokens + message.TokenUsage.OutputTokens;
        workflowBag[WorkflowVarSwarmTokensUsed] = JsonSerializer.SerializeToElement(tokensUsed);
        saga.WorkflowInputsJson = SerializeContextInputs(workflowBag);

        // Append the decision row for the just-finished contributor. The trace inspector renders
        // these top-to-bottom under the swarm node, one per contributor, with the synthesizer
        // row added below when it completes.
        saga.AppendDecision(new DecisionRecord(
            AgentKey: message.AgentKey,
            AgentVersion: message.AgentVersion,
            Decision: message.OutputPortName,
            DecisionPayload: CloneDecisionPayload(message.DecisionPayload),
            RoundId: saga.CurrentRoundId,
            RecordedAtUtc: DateTime.UtcNow,
            NodeId: swarmNode.Id,
            OutputPortName: message.OutputPortName,
            InputRef: saga.CurrentInputRef,
            OutputRef: message.OutputRef?.ToString(),
            NodeEnteredAtUtc: saga.CurrentRoundEnteredAtUtc));

        // Token-budget guard: if the cap is set and we've blown through it, run the synthesizer
        // immediately with swarmEarlyTerminated = true. Behaviour matches docs/swarm-node.md
        // §"Token budget" case 1 (Sequential, contributors not all dispatched yet).
        var budgetExceeded = swarmNode.SwarmTokenBudget is int budget
            && budget > 0
            && tokensUsed >= budget;

        if (position >= n || budgetExceeded)
        {
            if (saga.CurrentInputRef is null
                || !Uri.TryCreate(saga.CurrentInputRef, UriKind.Absolute, out var missionRef))
            {
                return $"Swarm node {swarmNode.Id}: missing mission input ref at synthesizer dispatch.";
            }

            await PublishSwarmSynthesizerAsync(
                context,
                agentConfigRepo,
                saga,
                workflow,
                swarmNode,
                missionRef,
                earlyTerminated: budgetExceeded && position < n);
            return null;
        }

        // Otherwise: dispatch the next contributor.
        if (saga.CurrentInputRef is null
            || !Uri.TryCreate(saga.CurrentInputRef, UriKind.Absolute, out var nextMissionRef))
        {
            return $"Swarm node {swarmNode.Id}: missing mission input ref between contributor positions.";
        }

        await PublishSwarmContributorAsync(
            context,
            agentConfigRepo,
            saga,
            workflow,
            swarmNode,
            nextMissionRef,
            roundId: Guid.NewGuid(),
            position: position + 1,
            n: n);
        return null;
    }

    /// <summary>
    /// Publishes an <see cref="AgentInvokeRequested"/> for a contributor at the given position.
    /// Used both by the initial entry and by follow-up contributors. Each contributor gets a
    /// fresh round id so the trace timeline renders them as separate rows (mirrors the design
    /// doc's "every contributor counts as a fresh round for trace clarity").
    /// </summary>
    private static async Task PublishSwarmContributorAsync(
        BehaviorContext<WorkflowSagaStateEntity> context,
        IAgentConfigRepository agentConfigRepo,
        WorkflowSagaStateEntity saga,
        Workflow workflow,
        WorkflowNode swarmNode,
        Uri inputRef,
        Guid roundId,
        int position,
        int n)
    {
        var contributorKey = swarmNode.ContributorAgentKey!;
        var pinnedVersion = saga.GetPinnedVersion(contributorKey)
            ?? swarmNode.ContributorAgentVersion
            ?? await agentConfigRepo.GetLatestVersionAsync(contributorKey, context.CancellationToken);

        if (saga.GetPinnedVersion(contributorKey) is null)
        {
            saga.PinAgentVersion(contributorKey, pinnedVersion);
        }

        var nowUtc = DateTime.UtcNow;
        saga.CurrentNodeId = swarmNode.Id;
        saga.CurrentAgentKey = contributorKey;
        saga.CurrentRoundId = roundId;
        saga.CurrentRoundEnteredAtUtc = nowUtc;
        saga.UpdatedAtUtc = nowUtc;

        await context.Publish(new AgentInvokeRequested(
            TraceId: saga.TraceId,
            RoundId: roundId,
            WorkflowKey: saga.WorkflowKey,
            WorkflowVersion: saga.WorkflowVersion,
            NodeId: swarmNode.Id,
            AgentKey: contributorKey,
            AgentVersion: pinnedVersion,
            InputRef: inputRef,
            ContextInputs: DeserializeContextInputs(saga.InputsJson),
            CorrelationHeaders: null,
            RetryContext: null,
            ToolExecutionContext: null,
            WorkflowContext: DeserializeContextInputs(saga.WorkflowInputsJson),
            ReviewRound: saga.ParentReviewRound,
            ReviewMaxRounds: saga.ParentReviewMaxRounds,
            OptOutLastRoundReminder: swarmNode.OptOutLastRoundReminder,
            SwarmContext: new SwarmInvocationContext(
                Position: position,
                MaxN: n,
                Assignment: null,
                EarlyTerminated: null),
            Repositories: ParseRepositoriesJson(saga.RepositoriesJson)));
    }

    /// <summary>
    /// Publishes the swarm's terminal synthesizer dispatch. The synthesizer reads the assembled
    /// <c>workflow.swarmContributions</c> array, optionally branches on <c>swarmEarlyTerminated</c>,
    /// and emits the swarm node's terminal artifact.
    /// </summary>
    private static async Task PublishSwarmSynthesizerAsync(
        BehaviorContext<WorkflowSagaStateEntity> context,
        IAgentConfigRepository agentConfigRepo,
        WorkflowSagaStateEntity saga,
        Workflow workflow,
        WorkflowNode swarmNode,
        Uri inputRef,
        bool earlyTerminated)
    {
        if (string.IsNullOrWhiteSpace(swarmNode.SynthesizerAgentKey)
            || swarmNode.SynthesizerAgentVersion is not int)
        {
            throw new InvalidOperationException(
                $"Swarm node {swarmNode.Id} is missing pinned synthesizer agent at runtime.");
        }

        var synthesizerKey = swarmNode.SynthesizerAgentKey!;
        var pinnedVersion = saga.GetPinnedVersion(synthesizerKey)
            ?? swarmNode.SynthesizerAgentVersion
            ?? await agentConfigRepo.GetLatestVersionAsync(synthesizerKey, context.CancellationToken);

        if (saga.GetPinnedVersion(synthesizerKey) is null)
        {
            saga.PinAgentVersion(synthesizerKey, pinnedVersion);
        }

        var roundId = Guid.NewGuid();
        var nowUtc = DateTime.UtcNow;
        saga.CurrentNodeId = swarmNode.Id;
        saga.CurrentAgentKey = synthesizerKey;
        saga.CurrentRoundId = roundId;
        saga.CurrentRoundEnteredAtUtc = nowUtc;
        saga.UpdatedAtUtc = nowUtc;

        await context.Publish(new AgentInvokeRequested(
            TraceId: saga.TraceId,
            RoundId: roundId,
            WorkflowKey: saga.WorkflowKey,
            WorkflowVersion: saga.WorkflowVersion,
            NodeId: swarmNode.Id,
            AgentKey: synthesizerKey,
            AgentVersion: pinnedVersion,
            InputRef: inputRef,
            ContextInputs: DeserializeContextInputs(saga.InputsJson),
            CorrelationHeaders: null,
            RetryContext: null,
            ToolExecutionContext: null,
            WorkflowContext: DeserializeContextInputs(saga.WorkflowInputsJson),
            ReviewRound: saga.ParentReviewRound,
            ReviewMaxRounds: saga.ParentReviewMaxRounds,
            OptOutLastRoundReminder: swarmNode.OptOutLastRoundReminder,
            SwarmContext: new SwarmInvocationContext(
                Position: null,
                MaxN: null,
                Assignment: null,
                EarlyTerminated: earlyTerminated),
            Repositories: ParseRepositoriesJson(saga.RepositoriesJson)));
    }

    /// <summary>
    /// Wipes swarm state from the saga + workflow context. Called when the synthesizer's
    /// completion arrives (so the swarm node's outgoing edges route as if it were any other
    /// agent-bearing node) and on swarm-internal failure paths.
    /// </summary>
    private static void ClearSwarmState(WorkflowSagaStateEntity saga)
    {
        saga.CurrentSwarmNodeId = null;
        saga.PendingParallelRoundIdsJson = null;

        var workflowBag = new Dictionary<string, JsonElement>(
            DeserializeContextInputs(saga.WorkflowInputsJson),
            StringComparer.Ordinal);

        var changed = false;
        foreach (var key in new[] { WorkflowVarSwarmMission, WorkflowVarSwarmContributions, WorkflowVarSwarmTokensUsed })
        {
            if (workflowBag.Remove(key))
            {
                changed = true;
            }
        }

        if (changed)
        {
            saga.WorkflowInputsJson = SerializeContextInputs(workflowBag);
        }
    }

    /// <summary>
    /// Initial swarm-context seed: writes the mission text and an empty contributions array
    /// onto the workflow bag, plus a zero token-counter. Overwrites any existing values so a
    /// nested or sibling swarm starts clean.
    /// </summary>
    private static void SeedSwarmWorkflowContext(WorkflowSagaStateEntity saga, string missionText)
    {
        var workflowBag = new Dictionary<string, JsonElement>(
            DeserializeContextInputs(saga.WorkflowInputsJson),
            StringComparer.Ordinal)
        {
            [WorkflowVarSwarmMission] = JsonSerializer.SerializeToElement(missionText ?? string.Empty),
            [WorkflowVarSwarmContributions] = JsonSerializer.SerializeToElement(Array.Empty<object>()),
            [WorkflowVarSwarmTokensUsed] = JsonSerializer.SerializeToElement(0L),
        };

        saga.WorkflowInputsJson = SerializeContextInputs(workflowBag);
    }

    /// <summary>
    /// Parses the optional leading <c>ROLE: &lt;value&gt;</c> line out of a contributor's output.
    /// Returns <c>(role: null, abstained: false)</c> for outputs without that prefix. The
    /// abstention marker is the exact literal <c>ROLE: abstain</c> on the first line — anything
    /// else (case variants, suffixes) is treated as a regular role label. Strict by design so the
    /// parser stays predictable; the contributor's prompt template enforces the format.
    /// </summary>
    internal static (string? Role, bool Abstained) ParseRoleLine(string artifactText)
    {
        if (string.IsNullOrEmpty(artifactText))
        {
            return (null, false);
        }

        var firstLineEnd = artifactText.IndexOf('\n');
        var firstLine = (firstLineEnd < 0 ? artifactText : artifactText[..firstLineEnd])
            .TrimEnd('\r', ' ', '\t');

        const string prefix = "ROLE:";
        if (!firstLine.StartsWith(prefix, StringComparison.Ordinal))
        {
            return (null, false);
        }

        var value = firstLine[prefix.Length..].Trim();
        if (string.Equals(value, "abstain", StringComparison.Ordinal))
        {
            return (value, true);
        }

        return (string.IsNullOrEmpty(value) ? null : value, false);
    }

    private static List<JsonElement> ReadSwarmContributionsArray(
        IReadOnlyDictionary<string, JsonElement> workflowBag)
    {
        if (!workflowBag.TryGetValue(WorkflowVarSwarmContributions, out var element)
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

    private static int CountSwarmContributions(WorkflowSagaStateEntity saga)
    {
        var bag = DeserializeContextInputs(saga.WorkflowInputsJson);
        return ReadSwarmContributionsArray(bag).Count;
    }

    private static long ReadSwarmTokensUsed(IReadOnlyDictionary<string, JsonElement> workflowBag)
    {
        if (!workflowBag.TryGetValue(WorkflowVarSwarmTokensUsed, out var element))
        {
            return 0L;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var v) => v,
            _ => 0L,
        };
    }

    private static JsonElement BuildContributionEntry(
        int position,
        string? role,
        bool abstained,
        string text,
        string agentKey,
        int agentVersion)
    {
        var obj = new
        {
            position,
            role,
            abstained,
            text,
            agentKey,
            agentVersion,
        };
        return JsonSerializer.SerializeToElement(obj);
    }

    private static JsonElement SerializeJsonArray(IReadOnlyList<JsonElement> entries)
    {
        using var stream = new System.IO.MemoryStream();
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
    /// Coordinator-protocol entry: dispatches the configured coordinator agent for a single round.
    /// The coordinator's output (an assignments payload — see <see cref="ParseCoordinatorAssignments"/>)
    /// is parsed by <see cref="HandleSwarmCoordinatorCompletionAsync"/> when the completion arrives;
    /// that handler then fans out N parallel workers.
    /// </summary>
    private static async Task PublishSwarmCoordinatorAsync(
        BehaviorContext<WorkflowSagaStateEntity> context,
        IAgentConfigRepository agentConfigRepo,
        WorkflowSagaStateEntity saga,
        Workflow workflow,
        WorkflowNode swarmNode,
        Uri inputRef,
        Guid roundId,
        int n)
    {
        var coordinatorKey = swarmNode.CoordinatorAgentKey!;
        var pinnedVersion = saga.GetPinnedVersion(coordinatorKey)
            ?? swarmNode.CoordinatorAgentVersion
            ?? await agentConfigRepo.GetLatestVersionAsync(coordinatorKey, context.CancellationToken);

        if (saga.GetPinnedVersion(coordinatorKey) is null)
        {
            saga.PinAgentVersion(coordinatorKey, pinnedVersion);
        }

        var nowUtc = DateTime.UtcNow;
        saga.CurrentNodeId = swarmNode.Id;
        saga.CurrentAgentKey = coordinatorKey;
        saga.CurrentRoundId = roundId;
        saga.CurrentRoundEnteredAtUtc = nowUtc;
        saga.UpdatedAtUtc = nowUtc;

        await context.Publish(new AgentInvokeRequested(
            TraceId: saga.TraceId,
            RoundId: roundId,
            WorkflowKey: saga.WorkflowKey,
            WorkflowVersion: saga.WorkflowVersion,
            NodeId: swarmNode.Id,
            AgentKey: coordinatorKey,
            AgentVersion: pinnedVersion,
            InputRef: inputRef,
            ContextInputs: DeserializeContextInputs(saga.InputsJson),
            CorrelationHeaders: null,
            RetryContext: null,
            ToolExecutionContext: null,
            WorkflowContext: DeserializeContextInputs(saga.WorkflowInputsJson),
            ReviewRound: saga.ParentReviewRound,
            ReviewMaxRounds: saga.ParentReviewMaxRounds,
            OptOutLastRoundReminder: swarmNode.OptOutLastRoundReminder,
            SwarmContext: new SwarmInvocationContext(
                Position: null,
                MaxN: n,
                Assignment: null,
                EarlyTerminated: null),
            Repositories: ParseRepositoriesJson(saga.RepositoriesJson)));
    }

    /// <summary>
    /// Handles the coordinator's completion: appends the coordinator's decision row, parses its
    /// assignments payload, generates K = min(parsed, n) round IDs, persists the pending set on
    /// the saga, and dispatches the K workers in parallel. Returns a non-null failure reason on
    /// unrecoverable parsing errors (malformed JSON, empty assignment list, or read failure on the
    /// artifact).
    /// </summary>
    private static async Task<string?> HandleSwarmCoordinatorCompletionAsync(
        BehaviorContext<WorkflowSagaStateEntity> context,
        IAgentConfigRepository agentConfigRepo,
        IArtifactStore artifactStore,
        WorkflowSagaStateEntity saga,
        Workflow workflow,
        WorkflowNode swarmNode,
        AgentInvocationCompleted message)
    {
        if (swarmNode.SwarmN is not int n || n < 1)
        {
            return $"Swarm node {swarmNode.Id} has no valid n at runtime — internal error.";
        }

        string artifactText;
        try
        {
            artifactText = message.OutputRef is null
                ? string.Empty
                : await ReadArtifactAsTextAsync(artifactStore, message.OutputRef, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Swarm node {swarmNode.Id}: failed to read coordinator output artifact: {ex.Message}";
        }

        if (!ParseCoordinatorAssignments(artifactText, out var assignments, out var parseError))
        {
            return $"Swarm node {swarmNode.Id}: coordinator returned malformed assignments — {parseError}";
        }

        // The coordinator may pick fewer than n. Cap at n; reject empty (the design says an empty
        // list is a hard failure, not "swarm produced no output").
        if (assignments.Count == 0)
        {
            return $"Swarm node {swarmNode.Id}: coordinator returned no assignments. Treat as Failed rather than dispatching zero workers.";
        }

        if (assignments.Count > n)
        {
            assignments = assignments.GetRange(0, n);
        }

        // Token tracking for the coordinator call. Same cumulative counter Sequential uses.
        var workflowBag = new Dictionary<string, JsonElement>(
            DeserializeContextInputs(saga.WorkflowInputsJson),
            StringComparer.Ordinal);
        var tokensUsed = ReadSwarmTokensUsed(workflowBag);
        tokensUsed += message.TokenUsage.InputTokens + message.TokenUsage.OutputTokens;
        workflowBag[WorkflowVarSwarmTokensUsed] = JsonSerializer.SerializeToElement(tokensUsed);
        saga.WorkflowInputsJson = SerializeContextInputs(workflowBag);

        // Coordinator gets its own decision row. Trace inspector renders it before the workers.
        saga.AppendDecision(new DecisionRecord(
            AgentKey: message.AgentKey,
            AgentVersion: message.AgentVersion,
            Decision: message.OutputPortName,
            DecisionPayload: CloneDecisionPayload(message.DecisionPayload),
            RoundId: saga.CurrentRoundId,
            RecordedAtUtc: DateTime.UtcNow,
            NodeId: swarmNode.Id,
            OutputPortName: message.OutputPortName,
            InputRef: saga.CurrentInputRef,
            OutputRef: message.OutputRef?.ToString(),
            NodeEnteredAtUtc: saga.CurrentRoundEnteredAtUtc));

        if (saga.CurrentInputRef is null
            || !Uri.TryCreate(saga.CurrentInputRef, UriKind.Absolute, out var missionRef))
        {
            return $"Swarm node {swarmNode.Id}: missing mission input ref at coordinator dispatch.";
        }

        // Generate one round id per assignment, persist the pending set, dispatch all workers.
        var pendingEntries = new List<PendingParallelEntry>(assignments.Count);
        for (var i = 0; i < assignments.Count; i++)
        {
            pendingEntries.Add(new PendingParallelEntry(
                RoundId: Guid.NewGuid(),
                Position: i + 1));
        }
        WritePendingParallelEntries(saga, pendingEntries);

        // CurrentRoundId after dispatch points at the most-recently-published worker's id; the
        // stale-round guard's standard equality check will accept that worker's completion, and
        // the IsAcceptablePendingParallelRound extension covers the others.
        Guid lastRoundId = pendingEntries[^1].RoundId;
        var dispatchedAtUtc = DateTime.UtcNow;
        saga.CurrentRoundId = lastRoundId;
        saga.CurrentRoundEnteredAtUtc = dispatchedAtUtc;
        saga.CurrentAgentKey = swarmNode.ContributorAgentKey!;
        saga.UpdatedAtUtc = dispatchedAtUtc;

        var contributorKey = swarmNode.ContributorAgentKey!;
        var contributorPinnedVersion = saga.GetPinnedVersion(contributorKey)
            ?? swarmNode.ContributorAgentVersion
            ?? await agentConfigRepo.GetLatestVersionAsync(contributorKey, context.CancellationToken);
        if (saga.GetPinnedVersion(contributorKey) is null)
        {
            saga.PinAgentVersion(contributorKey, contributorPinnedVersion);
        }

        for (var i = 0; i < pendingEntries.Count; i++)
        {
            var entry = pendingEntries[i];
            var assignment = assignments[i];

            await context.Publish(new AgentInvokeRequested(
                TraceId: saga.TraceId,
                RoundId: entry.RoundId,
                WorkflowKey: saga.WorkflowKey,
                WorkflowVersion: saga.WorkflowVersion,
                NodeId: swarmNode.Id,
                AgentKey: contributorKey,
                AgentVersion: contributorPinnedVersion,
                InputRef: missionRef,
                ContextInputs: DeserializeContextInputs(saga.InputsJson),
                CorrelationHeaders: null,
                RetryContext: null,
                ToolExecutionContext: null,
                WorkflowContext: DeserializeContextInputs(saga.WorkflowInputsJson),
                ReviewRound: saga.ParentReviewRound,
                ReviewMaxRounds: saga.ParentReviewMaxRounds,
                OptOutLastRoundReminder: swarmNode.OptOutLastRoundReminder,
                SwarmContext: new SwarmInvocationContext(
                    Position: entry.Position,
                    MaxN: n,
                    Assignment: assignment,
                    EarlyTerminated: null),
                Repositories: ParseRepositoriesJson(saga.RepositoriesJson)));
        }

        return null;
    }

    /// <summary>
    /// Worker completion in Coordinator mode: looks the round id up in the pending set to recover
    /// its 1-indexed position, appends a contribution row at that position, removes the entry from
    /// the pending set. When the pending set drains, evaluates the token budget per
    /// docs/swarm-node.md §"Token budget" Coordinator cases and either fails the swarm or
    /// dispatches the synthesizer (with <c>EarlyTerminated</c> set when budget is over by &lt; 10%).
    /// </summary>
    private static async Task<string?> HandleSwarmWorkerCompletionAsync(
        BehaviorContext<WorkflowSagaStateEntity> context,
        IAgentConfigRepository agentConfigRepo,
        IArtifactStore artifactStore,
        WorkflowSagaStateEntity saga,
        Workflow workflow,
        WorkflowNode swarmNode,
        AgentInvocationCompleted message)
    {
        if (swarmNode.SwarmN is not int n || n < 1)
        {
            return $"Swarm node {swarmNode.Id} has no valid n at runtime — internal error.";
        }

        if (!TryFindPendingParallelEntry(saga, message.RoundId, out var pendingEntry))
        {
            // Defensive: caller (saga state machine) only routes here when the round is in the
            // pending set, but if state was concurrently mutated this branch keeps the saga
            // recoverable rather than throwing into the message-bus retry loop.
            return $"Swarm node {swarmNode.Id}: worker completion for round {message.RoundId} arrived but the round is not in the pending parallel set.";
        }

        string artifactText;
        try
        {
            artifactText = message.OutputRef is null
                ? string.Empty
                : await ReadArtifactAsTextAsync(artifactStore, message.OutputRef, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Swarm node {swarmNode.Id}: failed to read worker #{pendingEntry.Position} output artifact: {ex.Message}";
        }

        var (role, abstained) = ParseRoleLine(artifactText);

        var workflowBag = new Dictionary<string, JsonElement>(
            DeserializeContextInputs(saga.WorkflowInputsJson),
            StringComparer.Ordinal);

        var contributions = ReadSwarmContributionsArray(workflowBag);
        contributions.Add(BuildContributionEntry(
            position: pendingEntry.Position,
            role: role,
            abstained: abstained,
            text: artifactText,
            agentKey: message.AgentKey,
            agentVersion: message.AgentVersion));
        workflowBag[WorkflowVarSwarmContributions] = SerializeJsonArray(contributions);

        var tokensUsed = ReadSwarmTokensUsed(workflowBag);
        tokensUsed += message.TokenUsage.InputTokens + message.TokenUsage.OutputTokens;
        workflowBag[WorkflowVarSwarmTokensUsed] = JsonSerializer.SerializeToElement(tokensUsed);
        saga.WorkflowInputsJson = SerializeContextInputs(workflowBag);

        // Append decision row at the worker's actual round id (not saga.CurrentRoundId, which
        // points at the last-dispatched worker and would mis-attribute parallel rows on the trace).
        // NodeEnteredAtUtc is the saga's CurrentRoundEnteredAtUtc (= the dispatch instant of the
        // whole parallel batch), so the trace inspector can render workers as overlapping rows.
        saga.AppendDecision(new DecisionRecord(
            AgentKey: message.AgentKey,
            AgentVersion: message.AgentVersion,
            Decision: message.OutputPortName,
            DecisionPayload: CloneDecisionPayload(message.DecisionPayload),
            RoundId: message.RoundId,
            RecordedAtUtc: DateTime.UtcNow,
            NodeId: swarmNode.Id,
            OutputPortName: message.OutputPortName,
            InputRef: saga.CurrentInputRef,
            OutputRef: message.OutputRef?.ToString(),
            NodeEnteredAtUtc: saga.CurrentRoundEnteredAtUtc));

        // Remove this worker from the pending set; if any are still in flight, just wait.
        var remaining = ReadPendingParallelEntries(saga);
        remaining.RemoveAll(e => e.RoundId == message.RoundId);
        if (remaining.Count > 0)
        {
            WritePendingParallelEntries(saga, remaining);
            return null;
        }

        // Pending set drained — evaluate the token budget per docs/swarm-node.md §"Token budget"
        // Coordinator cases. Budget == null means unbounded; > 10% over → Failed; > 0% over →
        // synthesizer with EarlyTerminated; otherwise normal synthesizer dispatch.
        WritePendingParallelEntries(saga, []);

        if (saga.CurrentInputRef is null
            || !Uri.TryCreate(saga.CurrentInputRef, UriKind.Absolute, out var missionRef))
        {
            return $"Swarm node {swarmNode.Id}: missing mission input ref at synthesizer dispatch.";
        }

        var earlyTerminated = false;
        if (swarmNode.SwarmTokenBudget is int budget && budget > 0)
        {
            // Threshold: budget * 1.10. Compare on long math to avoid double rounding around the
            // boundary. budget is bounded to [1, int.MaxValue / 2] in practice (validator caps it
            // implicitly via int). budget * 11 / 10 is safe for any int budget.
            var hardCap = (long)budget * 11 / 10;
            if (tokensUsed > hardCap)
            {
                var overPct = budget == 0 ? 0 : (tokensUsed - budget) * 100 / budget;
                return $"Swarm token budget exceeded by {overPct}% before synthesizer dispatch (used {tokensUsed} of {budget}).";
            }
            if (tokensUsed > budget)
            {
                earlyTerminated = true;
            }
        }

        await PublishSwarmSynthesizerAsync(
            context,
            agentConfigRepo,
            saga,
            workflow,
            swarmNode,
            missionRef,
            earlyTerminated: earlyTerminated);
        return null;
    }

    /// <summary>
    /// Parses the coordinator agent's output text as a JSON array of assignments. Accepts:
    /// <list type="bullet">
    /// <item>An array of strings: <c>["assignment 1", "assignment 2"]</c> — each becomes the
    /// matching worker's <c>swarmAssignment</c> verbatim.</item>
    /// <item>An array of objects: <c>[{"role": "x", "subTask": "y"}, ...]</c> — each object is
    /// JSON-stringified and passed as the assignment text. The contributor's prompt template
    /// can re-parse if it wants to read individual fields.</item>
    /// </list>
    /// Anything else (top-level non-array, mixed primitive types in array, etc.) is treated as
    /// malformed and returns false with a diagnostic in <paramref name="error"/>.
    /// </summary>
    internal static bool ParseCoordinatorAssignments(
        string artifactText,
        out List<string> assignments,
        out string error)
    {
        assignments = new List<string>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(artifactText))
        {
            error = "coordinator output was empty.";
            return false;
        }

        // Strip a leading ROLE: line if the coordinator agent's prompt happens to follow the same
        // convention contributors do; the assignments JSON is whatever follows.
        var jsonText = StripLeadingRoleLine(artifactText.Trim());

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = $"expected a JSON array at the top level, got {doc.RootElement.ValueKind}.";
                return false;
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.String:
                        var stringValue = element.GetString();
                        if (string.IsNullOrWhiteSpace(stringValue))
                        {
                            error = "an assignment string was empty or whitespace.";
                            return false;
                        }
                        assignments.Add(stringValue);
                        break;
                    case JsonValueKind.Object:
                        assignments.Add(element.GetRawText());
                        break;
                    default:
                        error = $"assignment entries must be strings or objects, got {element.ValueKind}.";
                        return false;
                }
            }
        }
        catch (JsonException ex)
        {
            error = $"could not parse coordinator output as JSON: {ex.Message}";
            return false;
        }

        return true;
    }

    private static string StripLeadingRoleLine(string artifactText)
    {
        if (string.IsNullOrEmpty(artifactText))
        {
            return artifactText;
        }
        if (!artifactText.StartsWith("ROLE:", StringComparison.Ordinal))
        {
            return artifactText;
        }
        var newlineAt = artifactText.IndexOf('\n');
        return newlineAt < 0 ? string.Empty : artifactText[(newlineAt + 1)..].TrimStart();
    }

    /// <summary>
    /// Pending-parallel-set entry: pairs a worker's round id with its 1-indexed assignment
    /// position. Persisted as a JSON array on
    /// <see cref="WorkflowSagaStateEntity.PendingParallelRoundIdsJson"/>.
    /// </summary>
    private readonly record struct PendingParallelEntry(Guid RoundId, int Position);

    private static List<PendingParallelEntry> ReadPendingParallelEntries(WorkflowSagaStateEntity saga)
    {
        if (string.IsNullOrEmpty(saga.PendingParallelRoundIdsJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(saga.PendingParallelRoundIdsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var entries = new List<PendingParallelEntry>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                if (!element.TryGetProperty("r", out var roundEl)
                    || roundEl.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(roundEl.GetString(), out var roundId)) continue;
                if (!element.TryGetProperty("p", out var posEl)
                    || posEl.ValueKind != JsonValueKind.Number
                    || !posEl.TryGetInt32(out var position)) continue;
                entries.Add(new PendingParallelEntry(roundId, position));
            }
            return entries;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void WritePendingParallelEntries(
        WorkflowSagaStateEntity saga,
        IReadOnlyList<PendingParallelEntry> entries)
    {
        if (entries.Count == 0)
        {
            saga.PendingParallelRoundIdsJson = null;
            return;
        }

        var arr = entries.Select(e => new { r = e.RoundId.ToString("D"), p = e.Position });
        saga.PendingParallelRoundIdsJson = JsonSerializer.Serialize(arr);
    }

    private static bool TryFindPendingParallelEntry(
        WorkflowSagaStateEntity saga,
        Guid roundId,
        out PendingParallelEntry entry)
    {
        foreach (var candidate in ReadPendingParallelEntries(saga))
        {
            if (candidate.RoundId == roundId)
            {
                entry = candidate;
                return true;
            }
        }
        entry = default;
        return false;
    }
}
