using CodeFlow.Orchestration.DryRun;

namespace CodeFlow.Orchestration.Replay.Admission;

/// <summary>
/// Witness that a replay-with-edit request has passed admission: the parent trace exists, the
/// drift level is acceptable for the run mode (or <c>force</c> opt-in is set), every author
/// edit resolves to a recorded decision and a declared output port on the target workflow,
/// and the resulting per-agent mock queues are ready for the dry-run executor.
///
/// Produced only by <see cref="ReplayRequestValidator"/>; the executor orchestration in
/// <see cref="CodeFlow.Api.Endpoints.TracesReplayEndpoints"/> consumes this type rather than
/// the raw <c>ReplayRequest</c> body so the no-validation path is gone at compile time.
///
/// Re-mint discipline: the source request shape (parent trace id + body) plus the saga +
/// workflow + decision rows in persistence are sufficient to replay the validator on a
/// fresh process and obtain an equivalent admitted value (modulo wall-clock fields).
/// </summary>
public sealed class AdmittedReplayRequest
{
    /// <summary>Validator-only constructor.</summary>
    internal AdmittedReplayRequest(
        Guid parentTraceId,
        string workflowKey,
        int originalWorkflowVersion,
        int targetWorkflowVersion,
        IReadOnlyDictionary<string, int> pinnedAgentVersions,
        IReadOnlyDictionary<string, IReadOnlyList<DryRunMockResponse>> mocks,
        IReadOnlyList<RecordedDecisionRef> decisions,
        DriftReport drift,
        bool force,
        DateTimeOffset admittedAt)
    {
        ParentTraceId = parentTraceId;
        WorkflowKey = workflowKey;
        OriginalWorkflowVersion = originalWorkflowVersion;
        TargetWorkflowVersion = targetWorkflowVersion;
        PinnedAgentVersions = pinnedAgentVersions;
        Mocks = mocks;
        Decisions = decisions;
        Drift = drift;
        Force = force;
        AdmittedAt = admittedAt;
    }

    /// <summary>The original trace being replayed.</summary>
    public Guid ParentTraceId { get; }

    /// <summary>Workflow key shared by the original saga and the target workflow.</summary>
    public string WorkflowKey { get; }

    /// <summary>Workflow version the saga was pinned to.</summary>
    public int OriginalWorkflowVersion { get; }

    /// <summary>Workflow version the dry-run will execute against (may differ via override).</summary>
    public int TargetWorkflowVersion { get; }

    /// <summary>
    /// Snapshot of the saga's pinned agent versions at admission time. Part of the "authority
    /// snapshot ref" the card mentions — provides reproducibility for re-mint, and a canonical
    /// answer to "which agent versions did this run see".
    /// </summary>
    public IReadOnlyDictionary<string, int> PinnedAgentVersions { get; }

    /// <summary>Per-agent mock response queues with edits + additional mocks already applied.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DryRunMockResponse>> Mocks { get; }

    /// <summary>Recorded decision references in saga order, for the response payload.</summary>
    public IReadOnlyList<RecordedDecisionRef> Decisions { get; }

    /// <summary>Drift snapshot detected at admission time.</summary>
    public DriftReport Drift { get; }

    /// <summary>Whether the request opted into hard-drift replay via <c>force=true</c>.</summary>
    public bool Force { get; }

    /// <summary>UTC instant the validator minted this admission.</summary>
    public DateTimeOffset AdmittedAt { get; }
}

/// <summary>
/// Raw request the replay validator turns into an <see cref="AdmittedReplayRequest"/>.
/// All inputs are gathered by <see cref="CodeFlow.Api.Endpoints.TracesReplayEndpoints"/>
/// (saga lookup, workflow load, mock extraction, drift detection) and handed in one shot
/// so the validator stays a pure function on its inputs.
/// </summary>
public sealed record ReplayAdmissionRequest(
    Guid ParentTraceId,
    string WorkflowKey,
    int OriginalWorkflowVersion,
    int TargetWorkflowVersion,
    string TargetWorkflowDisplayLabel,
    IReadOnlyDictionary<string, int> PinnedAgentVersions,
    ReplayMockBundle MockBundle,
    IReadOnlyDictionary<string, IReadOnlySet<string>> DeclaredPortsByAgent,
    IReadOnlyList<ReplayEdit>? Edits,
    IReadOnlyDictionary<string, IReadOnlyList<DryRunMockResponse>>? AdditionalMocks,
    bool Force,
    DriftReport Drift);
