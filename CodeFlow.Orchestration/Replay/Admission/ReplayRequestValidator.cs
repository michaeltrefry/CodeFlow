using System.Text.Json.Nodes;
using CodeFlow.Runtime.Authority.Admission;

namespace CodeFlow.Orchestration.Replay.Admission;

/// <summary>
/// Mints <see cref="AdmittedReplayRequest"/> values from raw replay-with-edit requests.
/// Folds the existing inline checks in <c>TracesReplayEndpoints</c> into a single boundary:
/// drift-vs-force admission and edit-shape validation. Filesystem / DB lookups (saga,
/// workflow, mock extraction) stay in the endpoint where they belong; admission is a pure
/// function on its inputs so re-mint replays cleanly.
///
/// Refusal taxonomy:
/// <list type="bullet">
///   <item><description><c>replay-drift-hard-refused</c> — drift level is Hard and
///   <c>force=true</c> was not supplied. Detail carries the drift level + warnings the
///   endpoint reconstitutes the <c>ReplayResponse</c> from.</description></item>
///   <item><description><c>replay-edit-validation</c> — one or more edits failed shape
///   validation (missing agent key, ordinal out of range, decision not declared on the
///   target workflow). Detail carries the indexed error messages the endpoint groups into
///   the <c>ValidationProblem</c> response.</description></item>
/// </list>
/// </summary>
public sealed class ReplayRequestValidator : IAdmissionValidator<ReplayAdmissionRequest, AdmittedReplayRequest>
{
    private readonly Func<DateTimeOffset> nowProvider;

    public ReplayRequestValidator(Func<DateTimeOffset>? nowProvider = null)
    {
        this.nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public Admission<AdmittedReplayRequest> Validate(ReplayAdmissionRequest input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Hard drift is the loudest admission decision: refusing it without force=true is the
        // only way authors learn that the workflow definition has structurally moved under
        // their recorded trace. Soft drift surfaces as warnings inside the admitted value.
        if (input.Drift.Level == DriftLevel.Hard && !input.Force)
        {
            return Admission<AdmittedReplayRequest>.Reject(new Rejection(
                Code: "replay-drift-hard-refused",
                Reason: "Hard drift detected; pass force=true to opt into a best-effort replay.",
                Axis: "replay",
                Path: input.ParentTraceId.ToString(),
                Detail: BuildDriftDetail(input.Drift)));
        }

        // ReplayEditsApplicator does the per-edit shape check (agent key resolves, ordinal in
        // range, decision in declared port set). Admission consumes the same applicator so
        // the validation surface here matches what the dry-run will actually run with.
        var applied = ReplayEditsApplicator.Apply(
            input.MockBundle.Mocks,
            input.Edits,
            input.AdditionalMocks,
            input.DeclaredPortsByAgent,
            input.TargetWorkflowDisplayLabel);

        if (applied.ValidationErrors.Count > 0)
        {
            return Admission<AdmittedReplayRequest>.Reject(new Rejection(
                Code: "replay-edit-validation",
                Reason: applied.ValidationErrors.Count == 1
                    ? applied.ValidationErrors[0]
                    : $"{applied.ValidationErrors.Count} edit validation errors.",
                Axis: "replay",
                Path: input.ParentTraceId.ToString(),
                Detail: BuildEditValidationDetail(applied.ValidationErrors)));
        }

        return Admission<AdmittedReplayRequest>.Accept(new AdmittedReplayRequest(
            parentTraceId: input.ParentTraceId,
            workflowKey: input.WorkflowKey,
            originalWorkflowVersion: input.OriginalWorkflowVersion,
            targetWorkflowVersion: input.TargetWorkflowVersion,
            pinnedAgentVersions: input.PinnedAgentVersions,
            mocks: applied.Mocks,
            decisions: input.MockBundle.Decisions,
            drift: input.Drift,
            force: input.Force,
            admittedAt: nowProvider()));
    }

    private static JsonObject BuildDriftDetail(DriftReport drift)
    {
        var warnings = new JsonArray();
        foreach (var warning in drift.Warnings)
        {
            warnings.Add(JsonValue.Create(warning));
        }
        return new JsonObject
        {
            ["driftLevel"] = drift.Level.ToString(),
            ["warnings"] = warnings,
        };
    }

    private static JsonObject BuildEditValidationDetail(IReadOnlyList<string> errors)
    {
        var array = new JsonArray();
        foreach (var error in errors)
        {
            array.Add(JsonValue.Create(error));
        }
        return new JsonObject
        {
            ["errors"] = array,
        };
    }
}
