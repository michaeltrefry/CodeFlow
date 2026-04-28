using System.Text.Json;

namespace CodeFlow.Contracts;

/// <summary>
/// Published every time a <c>TokenUsageRecord</c> is persisted by the orchestration-side capture
/// observer. The API-side trace-event observer translates this onto the in-memory broker so SSE
/// subscribers on <c>GET /api/traces/{id}/stream</c> see token usage land in real time.
/// </summary>
/// <param name="TraceId">Root trace this call belongs to. Used for SSE fanout.</param>
/// <param name="RecordId">Stable identifier of the persisted <c>TokenUsageRecord</c>.</param>
/// <param name="NodeId">Workflow node that issued the call.</param>
/// <param name="InvocationId">Per-LLM-call correlator minted by <c>InvocationLoop</c>.</param>
/// <param name="ScopeChain">Ordered parent-scope identifiers from root workflow down to the
/// originating node, excluding the root saga. Empty for top-level sagas.</param>
/// <param name="Provider">Free-form provider label (e.g., "openai", "anthropic", "lmstudio").</param>
/// <param name="Model">Free-form model identifier.</param>
/// <param name="RecordedAtUtc">UTC instant the call resolved.</param>
/// <param name="Usage">Provider-reported usage payload, verbatim.</param>
public sealed record TokenUsageRecorded(
    Guid TraceId,
    Guid RecordId,
    Guid NodeId,
    Guid InvocationId,
    IReadOnlyList<Guid> ScopeChain,
    string Provider,
    string Model,
    DateTime RecordedAtUtc,
    JsonElement Usage);
