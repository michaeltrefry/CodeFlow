using System.Text.Json;

namespace CodeFlow.Persistence;

/// <summary>
/// Domain projection of a captured token-usage row. One record per LLM round-trip.
/// </summary>
/// <param name="Id">Stable identifier for this record.</param>
/// <param name="TraceId">Root trace this call belongs to.</param>
/// <param name="NodeId">Workflow node that issued the call.</param>
/// <param name="InvocationId">Per-LLM-call correlator minted by <see cref="CodeFlow.Runtime.InvocationLoop"/>.</param>
/// <param name="ScopeChain">Ordered parent-scope identifiers from root workflow down to the
/// originating node. Empty for top-level sagas.</param>
/// <param name="Provider">Free-form provider label (e.g., "openai", "anthropic", "lmstudio").</param>
/// <param name="Model">Free-form model identifier.</param>
/// <param name="RecordedAtUtc">UTC instant the call resolved.</param>
/// <param name="Usage">Provider-reported usage payload, verbatim. Stored as a JSON document
/// so new fields land without a migration.</param>
public sealed record TokenUsageRecord(
    Guid Id,
    Guid TraceId,
    Guid NodeId,
    Guid InvocationId,
    IReadOnlyList<Guid> ScopeChain,
    string Provider,
    string Model,
    DateTime RecordedAtUtc,
    JsonElement Usage);
