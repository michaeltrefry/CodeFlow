import {
  ChangeDetectionStrategy,
  Component,
  Input,
  computed,
  inject,
  signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { TracesApi } from '../../core/traces.api';
import {
  TokenUsageEventPayload,
  TokenUsageRecordDto,
  TokenUsageRollup,
  TraceTokenUsageDto,
} from '../../core/models';
import { CardComponent } from '../../ui/card.component';
import { ChipComponent } from '../../ui/chip.component';
import { aggregateTokenUsage, recordDtoFromStreamEvent } from './token-usage-aggregator';

/**
 * Token Usage Tracking [Slice 6] — per-trace / per-node / per-scope detail panes.
 *
 * Loads `GET /api/traces/{id}/token-usage` once on open for the historical baseline,
 * then merges incoming `TokenUsageRecorded` SSE events into the same record list and
 * recomputes rollups in-memory so live overlays update without a server round-trip.
 *
 * Three render levels (acceptance):
 *  - Trace root: top-level total + provider+model breakdown.
 *  - Per-node:   each LLM-issuing node's captured usage, broken down when multiple
 *                provider+model combos contributed to it.
 *  - Per-scope:  rolled-up descendant totals for each subflow / ReviewLoop / Swarm
 *                scope id that appeared in any record's chain.
 *
 * Slices 7 (Path-through-workflow overlays) and 8 (Execution Timeline overlays)
 * read from the same `recordsSignal` exposed via `forNode()` / `forScope()` lookups.
 */
@Component({
  selector: 'cf-token-usage-panel',
  standalone: true,
  imports: [CommonModule, CardComponent, ChipComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <cf-card [title]="cardTitle()" flush>
      <ng-template #cardRight>
        @if (streamKind() === 'assistant') {
          <cf-chip variant="accent" data-testid="token-panel-stream-assistant">Assistant</cf-chip>
        }
        @if (aggregated().total.callCount > 0) {
          <cf-chip mono>{{ aggregated().total.callCount }} calls</cf-chip>
        }
      </ng-template>

      @if (loadError()) {
        <div class="empty-state">
          <cf-chip variant="err" dot>{{ loadError() }}</cf-chip>
        </div>
      } @else if (loading() && records().length === 0) {
        <div class="empty-state muted small">Loading…</div>
      } @else if (records().length === 0) {
        <div class="empty-state muted small">
          No LLM calls recorded for this trace yet. Token usage shows up here as the
          workflow runs.
        </div>
      } @else {
        <div class="section">
          <div class="section-head">
            <h4>Trace total</h4>
          </div>
          <div class="section-body">
            <ng-container
              *ngTemplateOutlet="rollupView; context: { rollup: aggregated().total, showCount: true }">
            </ng-container>
          </div>
        </div>

        @if (aggregated().byNode.length > 0) {
          <div class="section">
            <div class="section-head">
              <h4>Per node</h4>
              <span class="muted xsmall">
                Each LLM-issuing node and its captured usage
                @if (anyNodeHasMultipleCombos()) { · breakdown shown when a node spans multiple provider+model combos }
              </span>
            </div>
            <div class="section-body">
              @for (node of aggregated().byNode; track node.nodeId) {
                <div class="rollup-row">
                  <div class="rollup-row-head">
                    <span class="mono small">{{ nodeLabelFor(node.nodeId) }}</span>
                    <cf-chip mono>{{ node.rollup.callCount }} {{ node.rollup.callCount === 1 ? 'call' : 'calls' }}</cf-chip>
                  </div>
                  <ng-container
                    *ngTemplateOutlet="rollupView; context: { rollup: node.rollup, showCount: false }">
                  </ng-container>
                </div>
              }
            </div>
          </div>
        }

        @if (aggregated().byScope.length > 0) {
          <div class="section">
            <div class="section-head">
              <h4>Per scope</h4>
              <span class="muted xsmall">
                Subflow / ReviewLoop / Swarm — rolled up inclusive of descendants
              </span>
            </div>
            <div class="section-body">
              @for (scope of aggregated().byScope; track scope.scopeId) {
                <div class="rollup-row">
                  <div class="rollup-row-head">
                    <span class="mono small">{{ scopeLabelFor(scope.scopeId) }}</span>
                    <cf-chip mono>{{ scope.rollup.callCount }} {{ scope.rollup.callCount === 1 ? 'call' : 'calls' }}</cf-chip>
                  </div>
                  <ng-container
                    *ngTemplateOutlet="rollupView; context: { rollup: scope.rollup, showCount: false }">
                  </ng-container>
                </div>
              }
            </div>
          </div>
        }
      }

      <ng-template #rollupView let-rollup="rollup" let-showCount="showCount">
        @if (totalsAsPairs(rollup.totals).length === 0) {
          <span class="muted small">No numeric usage fields reported.</span>
        } @else {
          <ul class="totals-list">
            @for (pair of totalsAsPairs(rollup.totals); track pair.key) {
              <li>
                <span class="mono xsmall muted">{{ pair.key }}</span>
                <span class="mono small">{{ pair.value | number }}</span>
              </li>
            }
          </ul>

          @if (rollup.byProviderModel.length > 1) {
            <div class="combo-breakdown">
              @for (combo of rollup.byProviderModel; track combo.provider + combo.model) {
                <div class="combo">
                  <div class="combo-head">
                    <cf-chip mono>{{ combo.provider }} · {{ combo.model }}</cf-chip>
                  </div>
                  <ul class="totals-list nested">
                    @for (pair of totalsAsPairs(combo.totals); track pair.key) {
                      <li>
                        <span class="mono xsmall muted">{{ pair.key }}</span>
                        <span class="mono xsmall">{{ pair.value | number }}</span>
                      </li>
                    }
                  </ul>
                </div>
              }
            </div>
          }
        }
      </ng-template>
    </cf-card>
  `,
  styles: [`
    .empty-state { padding: 16px; }
    .section + .section { border-top: 1px solid var(--border); }
    .section-head { padding: 10px 16px 6px; display: flex; align-items: baseline; gap: 12px; }
    .section-head h4 { margin: 0; font-size: 13px; font-weight: 600; letter-spacing: 0.02em; text-transform: uppercase; color: var(--muted); }
    .section-body { padding: 0 16px 14px; display: flex; flex-direction: column; gap: 10px; }
    .rollup-row { padding: 10px 12px; border: 1px solid var(--border); border-radius: 6px; background: var(--surface-2); }
    .rollup-row-head { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-bottom: 8px; }
    .totals-list { list-style: none; margin: 0; padding: 0; display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 4px 16px; }
    .totals-list li { display: flex; align-items: baseline; justify-content: space-between; gap: 8px; }
    .totals-list.nested { margin-top: 4px; }
    .combo-breakdown { margin-top: 10px; padding-top: 10px; border-top: 1px dashed var(--border); display: flex; flex-direction: column; gap: 8px; }
    .combo-head { margin-bottom: 4px; }
  `],
})
export class TokenUsagePanelComponent {
  /** Required: which trace to load. */
  @Input({ required: true }) set traceId(value: string) {
    if (value && value !== this.currentTraceId) {
      this.currentTraceId = value;
      this.records.set([]);
      this.loadError.set(null);
      // Reset to default until the server tells us otherwise — this avoids a stale "Assistant"
      // label flashing when switching between traces of different streams.
      this.streamKind.set('workflow');
      this.loadHistorical(value);
    }
  }

  /** Optional resolver: turn a node id into a human-friendly label. The trace
   *  detail page passes its `labelForNode` so the panel doesn't need to know
   *  about the workflow shape. Falls back to a short hex prefix. */
  @Input() nodeLabel: ((nodeId: string) => string) | null = null;

  /** Optional resolver for scope ids (subflow / ReviewLoop child saga ids). The
   *  trace detail page maps child trace id → workflow key for these. */
  @Input() scopeLabel: ((scopeId: string) => string) | null = null;

  private readonly api = inject(TracesApi);
  private currentTraceId: string | null = null;

  /** Raw record list — single source of truth for both historical baseline and
   *  live SSE-merged updates. The aggregated view is a `computed` over this. Public
   *  so slices 7 (canvas overlays) and 8 (timeline overlays) can read the same
   *  records without re-fetching. */
  readonly records = signal<TokenUsageRecordDto[]>([]);
  protected readonly loading = signal(false);
  protected readonly loadError = signal<string | null>(null);

  /** HAA-14 — Stream label sourced from the server response. Drives the panel title and the
   *  "Assistant" chip. Defaults to `'workflow'` so a server that hasn't been upgraded yet (or
   *  a 404 path that never fetched) renders the original behavior unchanged. */
  protected readonly streamKind = signal<'workflow' | 'assistant'>('workflow');

  protected readonly cardTitle = computed(() =>
    this.streamKind() === 'assistant' ? 'Assistant token usage' : 'Token usage',
  );

  /** Aggregated rollup signal. Public so consuming overlays in the trace detail
   *  page can read per-node / per-scope rollups without duplicating the
   *  aggregator wiring. */
  readonly aggregated = computed<TraceTokenUsageDto>(() =>
    aggregateTokenUsage(this.currentTraceId ?? '', this.records(), this.streamKind()),
  );

  protected readonly anyNodeHasMultipleCombos = computed(
    () => this.aggregated().byNode.some(n => n.rollup.byProviderModel.length > 1),
  );

  /** Called by the parent (`trace-detail.component`) on each `TokenUsageRecorded`
   *  SSE event so live overlays update without a server round-trip. Idempotent on
   *  `recordId` so a stream replay or duplicate delivery doesn't double-count. */
  appendStreamEvent(payload: TokenUsageEventPayload, recordedAtUtc: string): void {
    const existing = this.records();
    if (existing.some(r => r.recordId === payload.recordId)) {
      return;
    }
    this.records.set([...existing, recordDtoFromStreamEvent(payload, recordedAtUtc)]);
  }

  protected nodeLabelFor(nodeId: string): string {
    return this.nodeLabel?.(nodeId) ?? this.shortId(nodeId);
  }

  protected scopeLabelFor(scopeId: string): string {
    return this.scopeLabel?.(scopeId) ?? this.shortId(scopeId);
  }

  protected totalsAsPairs(totals: Record<string, number>): { key: string; value: number }[] {
    // Stable display order: input-side fields first, then output-side, then
    // anything else alphabetically. This keeps the most-watched numbers (input/
    // output tokens) at the top regardless of the underlying iteration order.
    const pairs = Object.entries(totals).map(([key, value]) => ({ key, value }));
    pairs.sort((a, b) => fieldSortKey(a.key) - fieldSortKey(b.key) || a.key.localeCompare(b.key));
    return pairs;
  }

  private loadHistorical(traceId: string): void {
    this.loading.set(true);
    this.api.getTokenUsage(traceId).subscribe({
      next: dto => {
        // Server identifies whether this trace is a workflow saga or a synthetic assistant
        // conversation; the rest of the panel uses the in-memory aggregator so the label is
        // the only thing we read from `dto` here.
        this.streamKind.set(dto.streamKind ?? 'workflow');
        this.records.set(dto.records);
        this.loading.set(false);
      },
      error: err => {
        // 404 just means the trace doesn't exist yet (race on a freshly-created
        // saga); leave records empty and let the SSE stream fill them in.
        this.loading.set(false);
        if (err?.status !== 404) {
          this.loadError.set(err?.error?.error ?? err?.message ?? 'Failed to load token usage');
        }
      },
    });
  }

  private shortId(id: string): string {
    return id.length > 8 ? id.substring(0, 8) : id;
  }
}

function fieldSortKey(field: string): number {
  // Lower = sorted earlier.
  if (field.startsWith('input_tokens') || field === 'prompt_tokens') return 0;
  if (field.startsWith('output_tokens') || field === 'completion_tokens') return 1;
  if (field === 'total_tokens') return 2;
  if (field.includes('cache')) return 3;
  if (field.includes('reasoning') || field.includes('thinking')) return 4;
  return 5;
}
