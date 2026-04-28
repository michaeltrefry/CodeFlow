import {
  TokenUsageEventPayload,
  TokenUsageInvocationRollup,
  TokenUsageNodeRollup,
  TokenUsageProviderModelTotals,
  TokenUsageRecordDto,
  TokenUsageRollup,
  TokenUsageScopeRollup,
  TraceTokenUsageDto,
} from '../../core/models';

/**
 * Client-side mirror of `CodeFlow.Api/TokenTracking/TokenUsageAggregator.cs`. The
 * server returns one of these on initial load; for live updates we re-aggregate from
 * the same raw record list (kept in a signal) so a `TokenUsageRecorded` SSE event
 * doesn't require a server round-trip.
 *
 * Numeric leaves in the provider's `usage` object are summed by dotted JSON path
 * (e.g. `output_tokens_details.reasoning_tokens`) so a new provider field lands
 * without a code change here. Strings, booleans, arrays, and nulls are skipped from
 * sums but preserved on the per-call DTO's verbatim `usage` field.
 */
export function aggregateTokenUsage(
  traceId: string,
  records: TokenUsageRecordDto[],
): TraceTokenUsageDto {
  if (records.length === 0) {
    return {
      traceId,
      total: emptyRollup(),
      records: [],
      byInvocation: [],
      byNode: [],
      byScope: [],
    };
  }

  return {
    traceId,
    total: buildRollup(records),
    records,
    byInvocation: buildInvocationRollups(records),
    byNode: buildNodeRollups(records),
    byScope: buildScopeRollups(records),
  };
}

/** Build a per-call DTO from a `TokenUsageRecorded` SSE event payload. The server
 *  emits `recordId / nodeId / invocationId / scopeChain / provider / model / usage`
 *  but not `recordedAtUtc` or per-call totals — derive both here. */
export function recordDtoFromStreamEvent(
  event: TokenUsageEventPayload,
  recordedAtUtc: string,
): TokenUsageRecordDto {
  return {
    recordId: event.recordId,
    nodeId: event.nodeId,
    invocationId: event.invocationId,
    scopeChain: event.scopeChain ?? [],
    provider: event.provider,
    model: event.model,
    recordedAtUtc,
    usage: event.usage,
    totals: flattenAndSum([event.usage]),
  };
}

function buildInvocationRollups(records: TokenUsageRecordDto[]): TokenUsageInvocationRollup[] {
  const groups = new Map<string, TokenUsageRecordDto[]>();
  for (const record of records) {
    const key = `${record.nodeId}::${record.invocationId}`;
    const list = groups.get(key) ?? [];
    list.push(record);
    groups.set(key, list);
  }
  return Array.from(groups.entries()).map(([key, group]) => {
    const [nodeId, invocationId] = key.split('::');
    return { nodeId, invocationId, rollup: buildRollup(group) };
  });
}

function buildNodeRollups(records: TokenUsageRecordDto[]): TokenUsageNodeRollup[] {
  const groups = new Map<string, TokenUsageRecordDto[]>();
  for (const record of records) {
    const list = groups.get(record.nodeId) ?? [];
    list.push(record);
    groups.set(record.nodeId, list);
  }
  return Array.from(groups.entries()).map(([nodeId, group]) => ({
    nodeId,
    rollup: buildRollup(group),
  }));
}

function buildScopeRollups(records: TokenUsageRecordDto[]): TokenUsageScopeRollup[] {
  // Each record's ScopeChain is `[parent, child, grandchild...]` excluding root. For
  // any scope id appearing in any record's chain, sum every record whose chain
  // contains that id (inclusive of descendants).
  const scopeIds = new Set<string>();
  for (const record of records) {
    for (const id of record.scopeChain) {
      scopeIds.add(id);
    }
  }
  return Array.from(scopeIds).map(scopeId => ({
    scopeId,
    rollup: buildRollup(records.filter(r => r.scopeChain.includes(scopeId))),
  }));
}

function buildRollup(records: TokenUsageRecordDto[]): TokenUsageRollup {
  if (records.length === 0) {
    return emptyRollup();
  }

  const usageObjects = records.map(r => r.usage);

  const byProviderModel = new Map<string, TokenUsageRecordDto[]>();
  for (const record of records) {
    const key = `${record.provider}::${record.model}`;
    const list = byProviderModel.get(key) ?? [];
    list.push(record);
    byProviderModel.set(key, list);
  }

  const breakdown: TokenUsageProviderModelTotals[] = Array.from(byProviderModel.entries()).map(
    ([key, group]) => {
      const [provider, model] = key.split('::');
      return {
        provider,
        model,
        totals: flattenAndSum(group.map(r => r.usage)),
      };
    },
  );

  return {
    callCount: records.length,
    totals: flattenAndSum(usageObjects),
    byProviderModel: breakdown,
  };
}

function emptyRollup(): TokenUsageRollup {
  return {
    callCount: 0,
    totals: {},
    byProviderModel: [],
  };
}

function flattenAndSum(usageObjects: Record<string, unknown>[]): Record<string, number> {
  const totals: Record<string, number> = {};
  for (const usage of usageObjects) {
    flattenInto(usage, '', totals);
  }
  return totals;
}

function flattenInto(value: unknown, prefix: string, totals: Record<string, number>): void {
  if (value === null || value === undefined) {
    return;
  }

  if (typeof value === 'number' && Number.isFinite(value)) {
    if (prefix.length === 0) {
      // A bare numeric usage payload has no key — skip silently rather than invent one.
      return;
    }
    // Token counts are integers in every provider we've seen. If a provider ever reports
    // a fractional value, round to keep the type integer (matches the C# aggregator).
    const rounded = Math.round(value);
    totals[prefix] = (totals[prefix] ?? 0) + rounded;
    return;
  }

  if (typeof value === 'object' && !Array.isArray(value)) {
    for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
      const nextPrefix = prefix.length === 0 ? key : `${prefix}.${key}`;
      flattenInto(child, nextPrefix, totals);
    }
  }

  // Strings, booleans, arrays: not summable. Skip silently — the server aggregator
  // does the same.
}
