using System.Text.Json;
using CodeFlow.Api.Dtos;
using CodeFlow.Persistence;

namespace CodeFlow.Api.TokenTracking;

/// <summary>
/// Pure in-memory rollup of <see cref="TokenUsageRecord"/> rows for a single trace. All
/// aggregations are computed on read so historical and live views share one code path. No
/// persisted rollups, no materialization — slice 1's contract.
/// </summary>
/// <remarks>
/// <para>
/// Each rollup is a flattened sum of every numeric leaf in the provider-reported
/// <c>UsageJson</c>, keyed by dotted JSON path (e.g.,
/// <c>output_tokens_details.reasoning_tokens</c>). This keeps the aggregator schema-less and
/// future-proof: when a provider adds a new field, summing it Just Works without an aggregator
/// change. The UI gets to choose which fields to render.
/// </para>
/// <para>
/// Each rollup also carries a per-(provider, model) breakdown — always populated, even when
/// there's only one combination, so the inspector doesn't have to special-case the single-combo
/// path.
/// </para>
/// </remarks>
public static class TokenUsageAggregator
{
    public static TraceTokenUsageDto Aggregate(Guid traceId, IReadOnlyList<TokenUsageRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            return new TraceTokenUsageDto(
                TraceId: traceId,
                Total: EmptyRollup(),
                Records: Array.Empty<TokenUsageRecordDto>(),
                ByInvocation: Array.Empty<TokenUsageInvocationRollupDto>(),
                ByNode: Array.Empty<TokenUsageNodeRollupDto>(),
                ByScope: Array.Empty<TokenUsageScopeRollupDto>());
        }

        var raw = new TokenUsageRecordDto[records.Count];
        for (var i = 0; i < records.Count; i++)
        {
            raw[i] = ToRecordDto(records[i]);
        }

        return new TraceTokenUsageDto(
            TraceId: traceId,
            Total: BuildRollup(records),
            Records: raw,
            ByInvocation: BuildInvocationRollups(records),
            ByNode: BuildNodeRollups(records),
            ByScope: BuildScopeRollups(records));
    }

    private static TokenUsageRecordDto ToRecordDto(TokenUsageRecord record)
    {
        return new TokenUsageRecordDto(
            RecordId: record.Id,
            NodeId: record.NodeId,
            InvocationId: record.InvocationId,
            ScopeChain: record.ScopeChain.ToArray(),
            Provider: record.Provider,
            Model: record.Model,
            RecordedAtUtc: record.RecordedAtUtc,
            Usage: record.Usage,
            // Per-call totals are the flattened fields of this single record's usage payload.
            // Useful for the timeline view that shows each call individually.
            Totals: FlattenAndSum(new[] { record }));
    }

    private static IReadOnlyList<TokenUsageInvocationRollupDto> BuildInvocationRollups(
        IReadOnlyList<TokenUsageRecord> records)
    {
        // (NodeId, InvocationId) is the natural per-invocation key. InvocationLoop iterations
        // share the same InvocationId across rounds — actually no, each round mints a new id, so
        // this is per-LLM-round. The aggregation API conventions call this "per agent invocation"
        // but we group by InvocationId because that's the stable per-call correlator.
        return records
            .GroupBy(r => (r.NodeId, r.InvocationId))
            .Select(group => new TokenUsageInvocationRollupDto(
                NodeId: group.Key.NodeId,
                InvocationId: group.Key.InvocationId,
                Rollup: BuildRollup(group.ToArray())))
            .ToArray();
    }

    private static IReadOnlyList<TokenUsageNodeRollupDto> BuildNodeRollups(
        IReadOnlyList<TokenUsageRecord> records)
    {
        return records
            .GroupBy(r => r.NodeId)
            .Select(group => new TokenUsageNodeRollupDto(
                NodeId: group.Key,
                Rollup: BuildRollup(group.ToArray())))
            .ToArray();
    }

    private static IReadOnlyList<TokenUsageScopeRollupDto> BuildScopeRollups(
        IReadOnlyList<TokenUsageRecord> records)
    {
        // Each record's ScopeChain is `[parent, child, grandchild...]` excluding root. A scope
        // rolls up every record whose chain contains that scope id (inclusive of descendants), so
        // a parent subflow includes everything its children captured. Root totals come through
        // the trace-level rollup; we don't synthesize a "root scope" entry.
        var scopeIds = records
            .SelectMany(r => r.ScopeChain)
            .Distinct()
            .ToArray();

        return scopeIds
            .Select(scopeId => new TokenUsageScopeRollupDto(
                ScopeId: scopeId,
                Rollup: BuildRollup(records.Where(r => r.ScopeChain.Contains(scopeId)).ToArray())))
            .ToArray();
    }

    private static TokenUsageRollupDto BuildRollup(IReadOnlyList<TokenUsageRecord> records)
    {
        if (records.Count == 0)
        {
            return EmptyRollup();
        }

        var byProviderModel = records
            .GroupBy(r => (r.Provider, r.Model))
            .Select(group => new TokenUsageProviderModelTotalsDto(
                Provider: group.Key.Provider,
                Model: group.Key.Model,
                Totals: FlattenAndSum(group.ToArray())))
            .ToArray();

        return new TokenUsageRollupDto(
            CallCount: records.Count,
            Totals: FlattenAndSum(records),
            ByProviderModel: byProviderModel);
    }

    private static TokenUsageRollupDto EmptyRollup()
    {
        return new TokenUsageRollupDto(
            CallCount: 0,
            Totals: new Dictionary<string, long>(StringComparer.Ordinal),
            ByProviderModel: Array.Empty<TokenUsageProviderModelTotalsDto>());
    }

    private static IReadOnlyDictionary<string, long> FlattenAndSum(IReadOnlyList<TokenUsageRecord> records)
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            FlattenInto(record.Usage, prefix: string.Empty, totals);
        }
        return totals;
    }

    private static void FlattenInto(JsonElement element, string prefix, Dictionary<string, long> totals)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var nextPrefix = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
                    FlattenInto(property.Value, nextPrefix, totals);
                }
                break;

            case JsonValueKind.Number:
                if (prefix.Length == 0)
                {
                    // A bare numeric usage payload has no key — provider-shape we haven't seen.
                    // Skip silently; we never make up keys.
                    return;
                }
                if (element.TryGetInt64(out var value))
                {
                    totals[prefix] = totals.TryGetValue(prefix, out var existing)
                        ? existing + value
                        : value;
                }
                else if (element.TryGetDouble(out var doubleValue))
                {
                    // Token counts are integers in every provider we've seen, but if a provider
                    // ever reports a fractional value (cost, ratio, etc.) we round to long so the
                    // aggregator stays integer-typed. Slice 5 deliberately doesn't surface those
                    // fields in the UI; this just keeps the aggregator from crashing.
                    var rounded = (long)Math.Round(doubleValue);
                    totals[prefix] = totals.TryGetValue(prefix, out var existing)
                        ? existing + rounded
                        : rounded;
                }
                break;

            // Strings, bools, arrays, nulls are not summable. Skip silently — we don't drop them
            // from the raw `Usage` payload (the per-call DTO carries the verbatim element), but
            // they don't contribute to a sum.
            default:
                break;
        }
    }
}
