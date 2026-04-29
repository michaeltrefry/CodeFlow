using System.Text.Json;

namespace CodeFlow.Api.Dtos;

/// <summary>
/// Aggregation response for <c>GET /api/traces/{id}/token-usage</c>. All rollups are computed on
/// read from the raw <c>TokenUsageRecord</c> rows for the trace — the inspector uses the same
/// shape for both historical (one-shot fetch) and live (initial load + delta from
/// <c>TokenUsageRecorded</c> SSE events) modes.
/// </summary>
/// <param name="StreamKind">HAA-14 — labels the trace as either a workflow saga
/// (<c>"workflow"</c>) or a synthetic assistant conversation trace (<c>"assistant"</c>) so the
/// token panel can title itself accordingly. Assistant token usage threads into the same
/// <c>TokenUsageRecord</c> table as workflow runs (per HAA-1), so this flag is the only
/// boundary marker between the two streams in the inspector.</param>
public sealed record TraceTokenUsageDto(
    Guid TraceId,
    string StreamKind,
    TokenUsageRollupDto Total,
    IReadOnlyList<TokenUsageRecordDto> Records,
    IReadOnlyList<TokenUsageInvocationRollupDto> ByInvocation,
    IReadOnlyList<TokenUsageNodeRollupDto> ByNode,
    IReadOnlyList<TokenUsageScopeRollupDto> ByScope);

/// <summary>
/// Per-call raw data plus a flattened-totals projection for that single call.
/// </summary>
public sealed record TokenUsageRecordDto(
    Guid RecordId,
    Guid NodeId,
    Guid InvocationId,
    IReadOnlyList<Guid> ScopeChain,
    string Provider,
    string Model,
    DateTime RecordedAtUtc,
    JsonElement Usage,
    IReadOnlyDictionary<string, long> Totals);

/// <summary>
/// A summed rollup over some subset of the trace's calls. <see cref="Totals"/> sums every
/// numeric leaf in the provider-reported usage payloads (keyed by dotted JSON path);
/// <see cref="ByProviderModel"/> partitions the same data by (provider, model) so the UI can
/// show per-combo breakdowns when a node / scope spans more than one.
/// </summary>
public sealed record TokenUsageRollupDto(
    int CallCount,
    IReadOnlyDictionary<string, long> Totals,
    IReadOnlyList<TokenUsageProviderModelTotalsDto> ByProviderModel);

public sealed record TokenUsageProviderModelTotalsDto(
    string Provider,
    string Model,
    IReadOnlyDictionary<string, long> Totals);

public sealed record TokenUsageInvocationRollupDto(
    Guid NodeId,
    Guid InvocationId,
    TokenUsageRollupDto Rollup);

public sealed record TokenUsageNodeRollupDto(
    Guid NodeId,
    TokenUsageRollupDto Rollup);

public sealed record TokenUsageScopeRollupDto(
    Guid ScopeId,
    TokenUsageRollupDto Rollup);
