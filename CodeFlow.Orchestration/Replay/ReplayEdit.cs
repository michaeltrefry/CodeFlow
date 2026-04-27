using System.Text.Json.Nodes;

namespace CodeFlow.Orchestration.Replay;

/// <summary>
/// One author-supplied override applied on top of the recorded responses for a single agent
/// invocation. <see cref="Ordinal"/> is the 1-based per-agent index surfaced on
/// <see cref="RecordedDecisionRef.OrdinalPerAgent"/>.
/// </summary>
/// <remarks>
/// Each field is optional. A null field leaves the recorded value untouched. <see cref="Decision"/>,
/// when supplied, must match one of the agent's declared output ports — this is validated against
/// the workflow definition the replay runs against, not the original.
/// </remarks>
public sealed record ReplayEdit(
    string AgentKey,
    int Ordinal,
    string? Decision,
    string? Output,
    JsonNode? Payload);
