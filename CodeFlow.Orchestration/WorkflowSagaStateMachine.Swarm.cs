using CodeFlow.Contracts;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace CodeFlow.Orchestration;

/// <summary>
/// Swarm-node dispatch helpers (sc-43, Sequential protocol). Coordinator dispatch lands in sc-46
/// on top of this scaffolding. Lives in its own partial-class file so the swarm flow doesn't bury
/// the existing single-step routing in <c>WorkflowSagaStateMachine.cs</c>.
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

        // sc-43 ships Sequential only; sc-46 lights up Coordinator. Until then, refuse the
        // dispatch with a clear failure rather than silently treating Coordinator as Sequential.
        if (string.Equals(protocol, SwarmProtocolCoordinator, StringComparison.Ordinal))
        {
            saga.PendingTransition = PendingTransitionFailed;
            saga.FailureReason =
                $"Swarm node {swarmNode.Id} configured with protocol 'Coordinator' is not yet "
                + "dispatchable. Coordinator runtime ships in sc-46 — re-save the workflow with "
                + "protocol 'Sequential' to run it now.";
            saga.UpdatedAtUtc = DateTime.UtcNow;
            return;
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

        var missionText = await ReadArtifactAsTextAsync(artifactStore, inputRef, context.CancellationToken);

        SeedSwarmWorkflowContext(saga, missionText);
        saga.CurrentSwarmNodeId = swarmNode.Id;
        saga.CurrentInputRef = inputRef.ToString();

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
    /// Swarm node — i.e. the agent key matches the configured contributor and the swarm has not
    /// yet reached the synthesizer's slot. Coordinator's coordinator-step also routes through
    /// here in sc-46; for sc-43 the only non-contributor swarm-internal completion is the
    /// synthesizer.
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
                EarlyTerminated: false)));
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
                EarlyTerminated: earlyTerminated)));
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

}
