using CodeFlow.Orchestration.DryRun;

namespace CodeFlow.Orchestration.Replay;

/// <summary>
/// Output of <see cref="ReplayMockExtractor"/>: per-agent mock queues ready to drop into
/// <see cref="DryRunRequest.MockResponses"/>, plus a parallel list of decision references the
/// edits applicator and the UI need to address individual entries.
/// </summary>
public sealed record ReplayMockBundle(
    IReadOnlyDictionary<string, IReadOnlyList<DryRunMockResponse>> Mocks,
    IReadOnlyList<RecordedDecisionRef> Decisions);
