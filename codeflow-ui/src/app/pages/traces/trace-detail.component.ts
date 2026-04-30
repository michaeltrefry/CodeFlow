import { Component, DestroyRef, OnDestroy, OnInit, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { CommonModule, DatePipe, JsonPipe } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { Observable, interval, retry, timer } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { TracesApi } from '../../core/traces.api';
import { WorkflowsApi } from '../../core/workflows.api';
import {
  TraceDetail,
  TraceStreamEvent,
  TraceSummary,
  WorkflowDetail,
  WorkflowNode
} from '../../core/models';
import { streamTrace } from '../../core/trace-stream';
import { AuthService } from '../../auth/auth.service';
import { PageContextService } from '../../core/page-context.service';
import { HitlReviewComponent } from '../hitl/hitl-review.component';
import { WorkflowReadonlyCanvasComponent } from '../workflows/editor/workflow-readonly-canvas.component';
import { PageHeaderComponent } from '../../ui/page-header.component';
import { ButtonComponent } from '../../ui/button.component';
import { ChipComponent } from '../../ui/chip.component';
import { StateChipComponent } from '../../ui/state-chip.component';
import { CardComponent } from '../../ui/card.component';
import { TraceTimelineComponent } from '../../ui/trace-timeline.component';
import { TraceTimelineBadge, TraceTimelineEvent, TraceTimelineExtraLink, verdictSourceBadge } from '../../ui/trace-timeline.types';
import { TraceReplayPanelComponent } from './trace-replay-panel.component';
import { TokenUsagePanelComponent } from './token-usage-panel.component';
import { TraceBundlePanelComponent } from './trace-bundle-panel.component';
import { WorkflowNodeTokenOverlay } from '../workflows/editor/workflow-node-schemes';
import {
  TokenUsageInvocationRollup,
  TokenUsageNodeRollup,
  TokenUsageRecordDto,
  TokenUsageRollup,
  TokenUsageScopeRollup,
} from '../../core/models';

const DESCENDANT_REFRESH_INTERVAL_MS = 15_000;

interface TimelineEntry {
  id: string;
  kind: 'Requested' | 'Completed';
  agentKey: string;
  agentVersion: number;
  nodeId?: string | null;
  decision?: string | null;
  decisionPayload?: unknown;
  timestampUtc: string;
  inputRef?: string | null;
  outputRef?: string | null;
  /** When set, this entry came from a descendant saga; label describes the lineage
   *  (e.g., "prd-newproject-flow › prd-socratic-loop"). Empty means the entry is
   *  from the trace being viewed itself. */
  originPath?: string;
  /** Trace id this entry was actually recorded on — same as the viewed trace, or a
   *  descendant. Used to drive "open child trace ↗" links per entry. */
  originTraceId?: string;
  /** sc-273 — coarse classification of the verdict source (mechanical vs model). Server
   *  computes this from the agent's role grants; the timeline renders a small chip
   *  when present so authors can tell at a glance which gate produced a decision. */
  verdictSource?: 'mechanical' | 'model' | null;
}

interface PendingHitlGroup {
  traceId: string;
  isSubflow: boolean;
  subflowPathLabel: string;
  tasks: import('../../core/models').HitlTask[];
}

interface ReviewLoopRoundEntry {
  traceId: string;
  round: number;
  maxRounds: number;
  currentState: string;
  isLastRoundSeen: boolean;
}

interface ReviewLoopGroup {
  nodeId: string;
  nodeLabel: string;
  subflowKey: string | null;
  loopDecision: string;
  rounds: ReviewLoopRoundEntry[];
}

@Component({
  selector: 'cf-trace-detail',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    DatePipe,
    JsonPipe,
    HitlReviewComponent,
    WorkflowReadonlyCanvasComponent,
    PageHeaderComponent,
    ButtonComponent,
    ChipComponent,
    StateChipComponent,
    CardComponent,
    TraceTimelineComponent,
    TraceReplayPanelComponent,
    TokenUsagePanelComponent,
    TraceBundlePanelComponent,
  ],
  template: `
    <div class="page">
      @if (detail(); as d) {
        <cf-page-header title="Trace">
          <button type="button" cf-button variant="ghost" icon="copy" (click)="copyId(d.traceId)">Copy ID</button>
          @if (d.currentState === 'Running') {
            <button type="button" cf-button variant="danger" (click)="terminate()" [disabled]="actionBusy()">
              {{ actionBusy() ? 'Terminating…' : 'Terminate trace' }}
            </button>
          } @else {
            <button type="button" cf-button variant="ghost" icon="play" (click)="toggleReplayPanel()">
              {{ replayPanelOpen() ? 'Hide replay panel' : 'Replay with edit' }}
            </button>
            <button type="button" cf-button variant="danger" icon="trash" (click)="deleteTrace()" [disabled]="actionBusy()">
              {{ actionBusy() ? 'Deleting…' : 'Delete trace' }}
            </button>
          }
          <div page-header-body>
            <p class="mono muted" style="font-size: 13px; margin-top: 4px">{{ d.traceId }}</p>
            <div class="trace-header-meta">
              <cf-state-chip [state]="d.currentState"></cf-state-chip>
              <cf-chip mono>workflow: {{ d.workflowKey }} v{{ d.workflowVersion }}</cf-chip>
              <cf-chip mono>round: {{ d.roundCount }}</cf-chip>
              <cf-chip mono>current: {{ d.currentAgentKey }}</cf-chip>
              <cf-chip>created {{ d.createdAtUtc | date:'medium' }}</cf-chip>
              <cf-chip>updated {{ d.updatedAtUtc | date:'medium' }}</cf-chip>
            </div>
          </div>
        </cf-page-header>

        @if (d.failureReason) {
          <div class="trace-failure">
            <strong>Failure:</strong> {{ d.failureReason }}
            @if (failureHttpDiagnosticsRef(); as diagnosticsRef) {
              <div style="margin-top: 6px">
                <a class="mono-link" href="" (click)="downloadFailureDiagnostics($event, diagnosticsRef)">Download HTTP diagnostics →</a>
              </div>
            }
          </div>
        }

        @if (d.pendingHitl.length > 0) {
          <cf-card title="Awaiting human review">
            @for (group of pendingHitlGroups(); track group.traceId) {
              @if (group.isSubflow) {
                <div class="hitl-group-header">
                  <cf-chip mono>Subflow</cf-chip>
                  <span class="mono small">{{ group.subflowPathLabel }}</span>
                  @if (group.traceId !== d.traceId) {
                    <a class="mono-link small"
                       [routerLink]="['/traces', group.traceId]"
                       title="Open the child trace that owns this HITL">
                      open child trace ↗
                    </a>
                  }
                </div>
              }
              @for (task of group.tasks; track task.id) {
                <cf-hitl-review [task]="task" (decided)="reload()" />
              }
            }
          </cf-card>
        }

        @if (workflow()) {
          <cf-card title="Path through the workflow">
            <p class="muted small" style="margin-bottom: 10px">
              Nodes that executed during this trace are highlighted; the rest are dimmed.
            </p>
            <div class="graph-host">
              <cf-workflow-readonly-canvas
                [workflow]="workflow()"
                [highlightedNodeIds]="highlightedNodeIds()"
                [tokenUsageByNodeId]="tokenOverlayByNodeId()"></cf-workflow-readonly-canvas>
            </div>
          </cf-card>
        }

        @if (d.logicEvaluations.length > 0) {
          <cf-card title="Script evaluations" flush>
            <p class="muted xsmall" style="padding: 12px 16px 0">
              Includes both Logic node evaluations and agent/HITL-attached routing scripts.
            </p>
            <table class="table">
              <thead><tr><th>Node</th><th>Port chosen</th><th>Duration</th><th>Outcome</th><th>Logs</th></tr></thead>
              <tbody>
                @for (evaluation of d.logicEvaluations; track evaluation.recordedAtUtc) {
                  <tr>
                    <td class="mono small">{{ labelForNode(evaluation.nodeId) }}</td>
                    <td>
                      @if (evaluation.outputPortName) {
                        <cf-chip variant="accent" mono>{{ evaluation.outputPortName }}</cf-chip>
                      } @else {
                        <span class="muted small">—</span>
                      }
                    </td>
                    <td class="muted small">{{ evaluation.duration }}</td>
                    <td>
                      @if (evaluation.failureKind) {
                        <cf-chip variant="err" dot>{{ evaluation.failureKind }}</cf-chip>
                        @if (evaluation.failureMessage) {
                          <div class="muted xsmall">{{ evaluation.failureMessage }}</div>
                        }
                      } @else {
                        <cf-chip variant="ok" dot>ok</cf-chip>
                      }
                    </td>
                    <td>
                      @if (evaluation.logs.length === 0) {
                        <span class="muted small">—</span>
                      } @else {
                        <ul class="log-list">
                          @for (log of evaluation.logs; track $index) {
                            <li class="mono xsmall">{{ log }}</li>
                          }
                        </ul>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </cf-card>
        }

        @if (reviewLoopGroups().length > 0) {
          <cf-card title="Review loops">
            <p class="muted xsmall" style="margin-bottom: 10px">
              Each round is a separate child saga. Rounds before the last one returned the
              configured loop-decision port. The last round shows the outcome.
            </p>
            @for (group of reviewLoopGroups(); track group.nodeId) {
              <div class="review-loop-group">
                <div class="row-spread">
                  <strong class="mono small">{{ group.nodeLabel }}</strong>
                  <span class="muted xsmall">loop decision: <code>{{ group.loopDecision }}</code></span>
                </div>
                <ul class="review-loop-rounds">
                  @for (round of group.rounds; track round.traceId) {
                    <li>
                      <a [routerLink]="['/traces', round.traceId]" class="mono-link small">
                        Round {{ round.round }} of {{ round.maxRounds }} ↗
                      </a>
                      <cf-chip>{{ round.currentState }}</cf-chip>
                      @if (!round.isLastRoundSeen) {
                        <cf-chip variant="err" dot
                                [title]="'Child returned ' + group.loopDecision + ', triggering the next round'">{{ group.loopDecision }}</cf-chip>
                      }
                    </li>
                  }
                </ul>
              </div>
            }
          </cf-card>
        }

        <cf-token-usage-panel
          [traceId]="d.traceId"
          [nodeLabel]="nodeLabelResolver"
          [scopeLabel]="scopeLabelResolver"></cf-token-usage-panel>

        <cf-trace-bundle-panel [traceId]="d.traceId"></cf-trace-bundle-panel>

        <cf-card title="Execution timeline" flush>
          <ng-template #cardRight><cf-chip mono>{{ timeline().length }} hops</cf-chip></ng-template>
          <cf-trace-timeline
            [events]="timelineEvents()"
            [fetchArtifact]="artifactFetcher"
            (downloadRef)="onTimelineDownload($event)"></cf-trace-timeline>
        </cf-card>

        @if (replayPanelOpen()) {
          <cf-trace-replay-panel
            [traceId]="d.traceId"
            [workflow]="workflow()"
            (close)="replayPanelOpen.set(false)"></cf-trace-replay-panel>
        }

        <cf-card title="Pinned agent versions">
          <pre class="mono" style="white-space: pre-wrap; word-break: break-word">{{ d.pinnedAgentVersions | json }}</pre>
        </cf-card>

        <cf-card title="Context inputs">
          <p class="muted small" style="margin-bottom: 10px">Current saga context available to workflow scripts and agent templates.</p>
          <pre class="mono" style="white-space: pre-wrap; word-break: break-word">{{ d.contextInputs | json }}</pre>
        </cf-card>

        @if (actionError()) {
          <cf-chip variant="err" dot>{{ actionError() }}</cf-chip>
        }
      } @else {
        <cf-card>
          <div class="muted">Loading trace…</div>
        </cf-card>
      }
    </div>
  `,
  styles: [`
    .graph-host { height: 460px; margin-top: 0.5rem; }
    .hitl-group-header {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      padding: 0.5rem 0;
      margin-top: 0.75rem;
      border-top: 1px solid var(--border);
    }
    .hitl-group-header:first-of-type { border-top: none; margin-top: 0; padding-top: 0; }
    .review-loop-group { padding: 6px 0; }
    .review-loop-group + .review-loop-group { border-top: 1px solid var(--hairline); margin-top: 6px; padding-top: 10px; }
    .review-loop-rounds { list-style: none; padding: 4px 0 0 0; margin: 0; display: flex; flex-direction: column; gap: 4px; }
    .review-loop-rounds li { display: flex; gap: 8px; align-items: center; }
    .row-spread { display: flex; align-items: center; justify-content: space-between; gap: 10px; }
    .log-list { margin: 0; padding-left: 1rem; list-style: disc; }
  `]
})
export class TraceDetailComponent implements OnInit, OnDestroy {
  private readonly api = inject(TracesApi);
  private readonly workflowsApi = inject(WorkflowsApi);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly pageContext = inject(PageContextService);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = input.required<string>();
  readonly detail = signal<TraceDetail | null>(null);
  readonly workflow = signal<WorkflowDetail | null>(null);
  readonly timeline = signal<TimelineEntry[]>([]);
  readonly actionBusy = signal(false);
  readonly actionError = signal<string | null>(null);
  readonly childTraces = signal<TraceSummary[]>([]);
  readonly replayPanelOpen = signal(false);
  /** Map of descendant traceId → details, populated recursively while a parent saga is
   *  paused inside a Subflow/ReviewLoop. Used to merge child decisions into the parent
   *  timeline so the user sees the full execution flow without drilling into each
   *  child trace manually. */
  private readonly descendantDetails = signal<Map<string, TraceDetail>>(new Map());

  /**
   * Mapped projection of {@link timeline} into the shape the shared `<cf-trace-timeline>`
   * component consumes. Pure derivation — the shared component owns expansion state and
   * artifact-fetch caching internally.
   */
  readonly timelineEvents = computed<TraceTimelineEvent[]>(() =>
    this.timeline().map(entry => this.mapToTimelineEvent(entry)),
  );

  /** Bound input on the shared timeline — wraps `TracesApi.getArtifact`. */
  readonly artifactFetcher = (uri: string): Observable<string> => this.api.getArtifact(this.id(), uri);

  readonly reviewLoopGroups = computed<ReviewLoopGroup[]>(() => {
    const detail = this.detail();
    const workflow = this.workflow();
    if (!detail || !workflow) return [];

    const reviewLoopNodes = workflow.nodes.filter(n => n.kind === 'ReviewLoop');
    if (reviewLoopNodes.length === 0) return [];

    const children = this.childTraces()
      .filter(c => c.parentTraceId === detail.traceId && c.parentReviewRound != null);

    const groups: ReviewLoopGroup[] = [];
    for (const node of reviewLoopNodes) {
      const forNode = children
        .filter(c => c.parentNodeId === node.id)
        .sort((a, b) => (a.parentReviewRound ?? 0) - (b.parentReviewRound ?? 0));

      if (forNode.length === 0) continue;

      const rounds: ReviewLoopRoundEntry[] = forNode.map((c, idx) => ({
        traceId: c.traceId,
        round: c.parentReviewRound ?? 0,
        maxRounds: c.parentReviewMaxRounds ?? node.reviewMaxRounds ?? 0,
        currentState: c.currentState,
        isLastRoundSeen: idx === forNode.length - 1
      }));

      groups.push({
        nodeId: node.id,
        nodeLabel: this.labelForReviewLoopNode(node),
        subflowKey: node.subflowKey ?? null,
        loopDecision: (node.loopDecision ?? '').trim() || 'Rejected',
        rounds
      });
    }
    return groups;
  });

  private labelForReviewLoopNode(node: WorkflowNode): string {
    const key = node.subflowKey ?? '(pick workflow)';
    const rounds = node.reviewMaxRounds ? `×${node.reviewMaxRounds}` : '×?';
    return `ReviewLoop ${rounds} — ${key}`;
  }

  readonly highlightedNodeIds = computed<string[] | null>(() => {
    const d = this.detail();
    if (!d) return null;
    const ids = new Set<string>();
    for (const decision of d.decisions) {
      if (decision.nodeId) ids.add(decision.nodeId);
    }
    for (const evaluation of d.logicEvaluations) {
      ids.add(evaluation.nodeId);
    }
    // While a Subflow/ReviewLoop is mid-execution, the parent saga hasn't yet recorded
    // its synthetic completion decision — so the corresponding parent node is missing
    // from the set above. Surface it via direct child traces' `parentNodeId` so the
    // graph highlights "currently in" nodes too, not just "already finished" ones.
    for (const child of this.childTraces()) {
      if (child.parentNodeId) ids.add(child.parentNodeId);
    }
    return ids.size > 0 ? Array.from(ids) : [];
  });

  readonly pendingHitlGroups = computed<PendingHitlGroup[]>(() => {
    const d = this.detail();
    if (!d || d.pendingHitl.length === 0) return [];

    const rootGroup: PendingHitlGroup = {
      traceId: d.traceId,
      isSubflow: false,
      subflowPathLabel: '',
      tasks: [],
    };
    const byOrigin = new Map<string, PendingHitlGroup>();
    byOrigin.set(d.traceId, rootGroup);

    for (const task of d.pendingHitl) {
      const origin = task.originTraceId ?? task.traceId;
      const path = task.subflowPath ?? [];
      if (origin === d.traceId || path.length === 0) {
        rootGroup.tasks.push(task);
        continue;
      }

      let group = byOrigin.get(origin);
      if (!group) {
        group = {
          traceId: origin,
          isSubflow: true,
          subflowPathLabel: path.join(' › '),
          tasks: [],
        };
        byOrigin.set(origin, group);
      }
      group.tasks.push(task);
    }

    const ordered: PendingHitlGroup[] = [];
    if (rootGroup.tasks.length > 0) ordered.push(rootGroup);
    for (const group of byOrigin.values()) {
      if (group !== rootGroup) ordered.push(group);
    }
    return ordered;
  });

  readonly failureHttpDiagnosticsRef = computed<string | null>(() => {
    const detail = this.detail();
    if (!detail) return null;

    for (let index = detail.decisions.length - 1; index >= 0; index -= 1) {
      const decision = detail.decisions[index];
      if (decision.decision !== 'Failed') continue;

      const ref = this.readNestedString(decision.decisionPayload, ['payload', 'http_diagnostics_ref']);
      if (ref) {
        return ref;
      }
    }

    return null;
  });

  /** Token Usage panel reference for live SSE-event forwarding. The signal-form
   *  `viewChild` resolves once the `@if (detail()` template branch renders.
   *  Slices 7 + 8 also read its `records` and `aggregated` signals to build the
   *  canvas overlay map and timeline-row token-usage payloads. */
  private readonly tokenUsagePanel = viewChild(TokenUsagePanelComponent);

  /**
   * Slice 7: per-node token-usage overlay for the readonly canvas. Combines
   *  - direct LLM-issuing nodes (`aggregated().byNode[nodeId]`)
   *  - Subflow / ReviewLoop / Swarm nodes that don't issue calls themselves but
   *    spawn child sagas (sum `byScope[childSagaId]` for every direct child
   *    whose `parentNodeId` matches the workflow node id).
   *
   * Returns `null` until the panel reports any records — keeps the canvas in
   * its un-decorated state for traces that haven't issued an LLM call yet.
   */
  readonly tokenOverlayByNodeId = computed<Map<string, WorkflowNodeTokenOverlay> | null>(() => {
    const panel = this.tokenUsagePanel();
    if (!panel) return null;
    const aggregated = panel.aggregated();
    if (aggregated.records.length === 0) return null;

    const map = new Map<string, WorkflowNodeTokenOverlay>();

    // Direct per-node rollups (LLM-issuing nodes).
    for (const nodeRollup of aggregated.byNode) {
      map.set(nodeRollup.nodeId, this.toCanvasOverlay(nodeRollup.rollup, false));
    }

    // Subflow / ReviewLoop / Swarm: roll up the child sagas they spawned. The
    // parent's workflow has no LLM-issuing node for these, but the descendant
    // saga's TraceId appears as a scope id in its records' chains. We match
    // children by parentNodeId so multi-round ReviewLoops accumulate across
    // every round on the parent's node.
    const childrenByParentNode = new Map<string, string[]>();
    for (const child of this.childTraces()) {
      if (!child.parentNodeId) continue;
      const list = childrenByParentNode.get(child.parentNodeId) ?? [];
      list.push(child.traceId);
      childrenByParentNode.set(child.parentNodeId, list);
    }

    const scopeById = new Map(
      aggregated.byScope.map(s => [s.scopeId, s] as [string, TokenUsageScopeRollup]),
    );

    for (const [parentNodeId, childTraceIds] of childrenByParentNode.entries()) {
      // Skip if this node already has a direct rollup — direct calls take
      // precedence over scope rollups (a node can't both directly issue and
      // delegate; if it does, the direct count is the authoritative one).
      if (map.has(parentNodeId)) continue;

      let calls = 0;
      let inputTokens = 0;
      let outputTokens = 0;
      for (const childTraceId of childTraceIds) {
        const scope = scopeById.get(childTraceId);
        if (!scope) continue;
        calls += scope.rollup.callCount;
        inputTokens += scope.rollup.totals['input_tokens'] ?? scope.rollup.totals['prompt_tokens'] ?? 0;
        outputTokens += scope.rollup.totals['output_tokens'] ?? scope.rollup.totals['completion_tokens'] ?? 0;
      }
      if (calls === 0) continue;
      map.set(parentNodeId, { callCount: calls, inputTokens, outputTokens, rolledUp: true });
    }

    return map;
  });

  private toCanvasOverlay(rollup: TokenUsageRollup, rolledUp: boolean): WorkflowNodeTokenOverlay {
    return {
      callCount: rollup.callCount,
      inputTokens: rollup.totals['input_tokens'] ?? rollup.totals['prompt_tokens'] ?? 0,
      outputTokens: rollup.totals['output_tokens'] ?? rollup.totals['completion_tokens'] ?? 0,
      rolledUp,
    };
  }

  /**
   * Slice 8: per-timeline-row token-usage payloads. Each completed decision row
   * "claims" every record with the same nodeId whose `recordedAtUtc` falls
   * after the previous claim and at-or-before this row's timestamp. Each token
   * record matches at most one row, so a multi-round ReviewLoop's final row
   * doesn't double-count records that earlier rounds already showed.
   */
  readonly tokenUsageByRowId = computed<Map<string, TokenUsageRollup>>(() => {
    const panel = this.tokenUsagePanel();
    const out = new Map<string, TokenUsageRollup>();
    if (!panel) return out;
    const records = panel.aggregated().records;
    if (records.length === 0) return out;

    // Sort timeline rows ascending by timestamp so the claim sweep is deterministic.
    const rows = [...this.timeline()]
      .filter(e => e.kind === 'Completed' && e.nodeId)
      .sort((a, b) => a.timestampUtc.localeCompare(b.timestampUtc));

    // Per-node high-water mark of the last claim's record timestamp (records
    // before this are already attributed to an earlier row; later records
    // belong to this row's window).
    const claimedBefore = new Map<string, string>();

    const sortedRecords = [...records].sort((a, b) =>
      a.recordedAtUtc.localeCompare(b.recordedAtUtc),
    );

    for (const row of rows) {
      const nodeId = row.nodeId!;
      const lowerBound = claimedBefore.get(nodeId) ?? '';
      const upperBound = row.timestampUtc;
      const matched: TokenUsageRecordDto[] = [];
      for (const record of sortedRecords) {
        if (record.nodeId !== nodeId) continue;
        if (record.recordedAtUtc <= lowerBound) continue;
        if (record.recordedAtUtc > upperBound) continue;
        matched.push(record);
      }
      if (matched.length === 0) continue;
      claimedBefore.set(nodeId, upperBound);
      out.set(row.id, this.summarizeRecords(matched));
    }

    return out;
  });

  private summarizeRecords(records: TokenUsageRecordDto[]): TokenUsageRollup {
    const totals: Record<string, number> = {};
    const combos = new Map<string, { provider: string; model: string; totals: Record<string, number> }>();
    for (const record of records) {
      this.addInto(totals, record.totals);
      const comboKey = `${record.provider}::${record.model}`;
      const combo = combos.get(comboKey) ?? { provider: record.provider, model: record.model, totals: {} };
      this.addInto(combo.totals, record.totals);
      combos.set(comboKey, combo);
    }
    return {
      callCount: records.length,
      totals,
      byProviderModel: Array.from(combos.values()),
    };
  }

  private addInto(target: Record<string, number>, source: Record<string, number>): void {
    for (const [key, value] of Object.entries(source)) {
      target[key] = (target[key] ?? 0) + value;
    }
  }

  /** Resolver bound on the panel: turn a node id into a workflow node label.
   *  Stable arrow-fn reference so the input doesn't churn across change detections. */
  protected readonly nodeLabelResolver = (nodeId: string): string => this.labelForNode(nodeId);

  /** Resolver for scope ids — these are descendant saga TraceIds (subflow / ReviewLoop
   *  child sagas). The descendant details map is the authoritative source for
   *  `workflowKey` per scope; fall back to a short hex prefix when not yet loaded. */
  protected readonly scopeLabelResolver = (scopeId: string): string => {
    const childDetail = this.descendantDetails().get(scopeId);
    if (childDetail?.workflowKey) {
      return `${childDetail.workflowKey} (${scopeId.substring(0, 8)})`;
    }
    return scopeId.length > 8 ? scopeId.substring(0, 8) : scopeId;
  };
  private loadedWorkflowKey: string | null = null;
  private loadedWorkflowVersion: number | null = null;

  /** Click handler for `<cf-trace-timeline>` `(downloadRef)` events — kicks off the
   *  blob download via TracesApi. The shared timeline emits the URI for both the
   *  per-row "Download input artifact" link and any extra-link (HTTP diagnostics). */
  onTimelineDownload(uri: string): void {
    this.downloadArtifact(uri);
  }

  downloadArtifact(uri: string): void {
    this.api.downloadArtifact(this.id(), uri).pipe(
      takeUntilDestroyed(this.destroyRef)
    ).subscribe({
      next: response => {
        const blob = response.body;
        if (!blob) {
          return;
        }

        const url = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = this.fileNameFromResponse(response.headers.get('content-disposition')) ?? this.fileNameForArtifact(uri);
        anchor.click();
        URL.revokeObjectURL(url);
      }
    });
  }

  /** Top-of-page failure-banner download — a separate template hook because the saga
   *  failure ref isn't anchored to a timeline row. */
  downloadFailureDiagnostics(event: Event, uri: string): void {
    event.preventDefault();
    this.downloadArtifact(uri);
  }

  constructor() {
    // Re-register on every `id()` change. With `withComponentInputBinding()` the route param
    // updates the signal in place when the user navigates from /traces/A to /traces/B without a
    // remount, and ngOnInit does NOT fire again — so this effect is the only correct hook for
    // keeping the assistant sidebar's PageContext in sync with the current trace.
    effect(() => {
      this.pageContext.set({ kind: 'trace', traceId: this.id() });
    });
  }

  ngOnInit(): void {
    this.reload();

    streamTrace(this.id(), this.auth).pipe(
      takeUntilDestroyed(this.destroyRef)
    ).subscribe({
      next: evt => this.appendEvent(evt)
    });

    interval(DESCENDANT_REFRESH_INTERVAL_MS).pipe(
      takeUntilDestroyed(this.destroyRef)
    ).subscribe(() => {
      const current = this.detail();
      if (current && current.currentState === 'Running') {
        this.loadDescendantTracesFor(current.traceId);
      }
    });
  }

  ngOnDestroy(): void {
    this.pageContext.clear();
  }

  reload(): void {
    this.actionError.set(null);
    this.api.get(this.id()).pipe(
      retry({
        count: 10,
        delay: (err, attempt) =>
          err instanceof HttpErrorResponse && err.status === 404
            ? timer(500 * Math.min(attempt, 4))
            : (() => { throw err; })()
      }),
      takeUntilDestroyed(this.destroyRef)
    ).subscribe({
      next: detail => {
        this.detail.set(detail);
        this.rebuildTimeline();
        this.loadWorkflowForTrace(detail);
        this.loadDescendantTracesFor(detail.traceId);
      }
    });
  }

  /** Parent-of mapping for every trace returned by the descendants endpoint — covers the
   *  full descendant tree, not just direct children. Rebuilt every descendant refresh. */
  private readonly parentByTraceId = signal<Map<string, string>>(new Map());

  /** Load the full descendant tree (children + grandchildren + ...) so the timeline can
   *  merge their decisions in chronological order. Without this, the parent's timeline
   *  appears stuck while a Subflow/ReviewLoop is mid-execution because the parent saga
   *  doesn't record any decision until the child returns. */
  private loadDescendantTracesFor(rootTraceId: string): void {
    this.api.getDescendants(rootTraceId).pipe(
      takeUntilDestroyed(this.destroyRef)
    ).subscribe({
      next: descendants => {
        const summaries = descendants.map(descendant => descendant.summary);
        const directChildren = summaries.filter(t => t.parentTraceId === rootTraceId);
        this.childTraces.set(directChildren);

        const parentMap = new Map<string, string>();
        for (const t of summaries) {
          if (t.parentTraceId) parentMap.set(t.traceId, t.parentTraceId);
        }
        this.parentByTraceId.set(parentMap);

        if (descendants.length === 0) {
          this.descendantDetails.set(new Map());
          this.rebuildTimeline();
          return;
        }

        this.descendantDetails.set(
          new Map(descendants.map(descendant => [descendant.summary.traceId, descendant.detail]))
        );
        this.rebuildTimeline();
      },
      error: () => {
        this.childTraces.set([]);
        this.descendantDetails.set(new Map());
        this.rebuildTimeline();
      }
    });
  }

  /** Rebuild the timeline from the parent trace's decisions + every descendant's
   *  decisions, sorted by recorded timestamp. Each non-root entry carries an
   *  `originPath` describing the lineage so the UI can render a "via X › Y" tag. */
  private rebuildTimeline(): void {
    const root = this.detail();
    if (!root) {
      this.timeline.set([]);
      return;
    }

    const descendants = this.descendantDetails();
    // workflowKey lookup for the lineage path; falls back to traceId fragments.
    const detailsByTraceId = new Map<string, TraceDetail>();
    detailsByTraceId.set(root.traceId, root);
    for (const [id, d] of descendants) detailsByTraceId.set(id, d);

    const parentMap = this.parentByTraceId();

    const lineagePathFor = (traceId: string): string => {
      const segments: string[] = [];
      let current = traceId;
      // Walk up to the root, prepending each ancestor's workflowKey.
      while (current && current !== root.traceId) {
        const detail = detailsByTraceId.get(current);
        segments.unshift(detail?.workflowKey ?? current.slice(0, 8));
        const parent = parentMap.get(current);
        if (!parent) break;
        current = parent;
      }
      return segments.join(' › ');
    };

    const merged: TimelineEntry[] = [];
    let counter = 0;

    const pushDecisions = (detail: TraceDetail, originPath: string) => {
      for (const d of detail.decisions) {
        merged.push({
          id: `${detail.traceId}-${counter++}`,
          kind: 'Completed',
          agentKey: d.agentKey,
          agentVersion: d.agentVersion,
          nodeId: d.nodeId,
          decision: d.decision,
          decisionPayload: d.decisionPayload,
          timestampUtc: d.recordedAtUtc,
          inputRef: d.inputRef,
          outputRef: d.outputRef,
          originPath: originPath || undefined,
          originTraceId: detail.traceId,
          verdictSource: d.verdictSource ?? null,
        });
      }
    };

    pushDecisions(root, '');
    for (const [traceId, detail] of descendants) {
      pushDecisions(detail, lineagePathFor(traceId));
    }

    merged.sort((a, b) => a.timestampUtc.localeCompare(b.timestampUtc));
    this.timeline.set(merged);
  }

  copyId(id: string): void {
    navigator.clipboard?.writeText(id).catch(() => undefined);
  }

  toggleReplayPanel(): void {
    this.replayPanelOpen.update(open => !open);
  }

  terminate(): void {
    const detail = this.detail();
    if (!detail || detail.currentState !== 'Running') return;
    if (!window.confirm(`Terminate trace ${detail.traceId}?`)) return;

    this.actionBusy.set(true);
    this.actionError.set(null);
    this.api.terminate(detail.traceId).pipe(
      takeUntilDestroyed(this.destroyRef)
    ).subscribe({
      next: () => { this.actionBusy.set(false); this.reload(); },
      error: err => {
        this.actionBusy.set(false);
        this.actionError.set(err?.error?.error ?? err?.message ?? 'Failed to terminate trace');
      }
    });
  }

  deleteTrace(): void {
    const detail = this.detail();
    if (!detail || detail.currentState === 'Running') return;
    if (!window.confirm(`Delete trace ${detail.traceId}? This removes its history.`)) return;

    this.actionBusy.set(true);
    this.actionError.set(null);
    this.api.delete(detail.traceId).pipe(
      takeUntilDestroyed(this.destroyRef)
    ).subscribe({
      next: () => { this.actionBusy.set(false); this.router.navigate(['/traces']); },
      error: err => {
        this.actionBusy.set(false);
        this.actionError.set(err?.error?.error ?? err?.message ?? 'Failed to delete trace');
      }
    });
  }

  labelForNode(nodeId: string): string {
    const wf = this.workflow();
    if (!wf) return nodeId;
    const node = wf.nodes.find(n => n.id === nodeId);
    if (!node) return nodeId;
    if (node.agentKey) return `${node.agentKey} (${node.kind})`;
    if (node.kind === 'ReviewLoop') return this.labelForReviewLoopNode(node);
    if (node.kind === 'Subflow') return this.labelForSubflowNode(node);
    return `${node.kind} ${nodeId.substring(0, 8)}`;
  }

  labelForTimelineEntry(entry: TimelineEntry): string {
    const explicit = entry.agentKey?.trim();
    if (explicit) return explicit;
    if (entry.nodeId) return this.labelForNode(entry.nodeId);
    return 'workflow step';
  }

  /**
   * Adapter from the saga-side {@link TimelineEntry} (one row per recorded decision)
   * to the shared {@link TraceTimelineEvent} shape consumed by `<cf-trace-timeline>`.
   * The saga case uses ref-based artifact lazy-loading via the timeline's
   * `fetchArtifact` input; previews on this page are NEVER inline.
   */
  private mapToTimelineEvent(entry: TimelineEntry): TraceTimelineEvent {
    const badges: TraceTimelineBadge[] = [
      { label: `v${entry.agentVersion}`, mono: true },
    ];
    if (entry.originPath) {
      badges.push({ label: `via ${entry.originPath}`, mono: true });
    }
    // sc-273 — distinguish mechanical-gate decisions (deterministic command execution)
    // from model-side reviewer decisions (LLM judgment). Helper returns null when the
    // server didn't tag the decision (mixed grant set), which the spread guards.
    const verdictBadge = verdictSourceBadge(entry.verdictSource);
    if (verdictBadge) {
      badges.push(verdictBadge);
    }

    const expandedExtras: TraceTimelineExtraLink[] = [];
    const httpDiagnosticsRef = this.httpDiagnosticsRefForDecision(entry.decisionPayload);
    if (httpDiagnosticsRef) {
      expandedExtras.push({ label: 'Download HTTP diagnostics', ref: httpDiagnosticsRef });
    }

    // Slice 8: hand the row's token-usage payload through if the per-row map
    // has claimed records for this id. The map is signal-derived so it
    // recomputes whenever new records arrive from the SSE stream.
    const tokenUsage = this.tokenUsageByRowId().get(entry.id) ?? null;

    return {
      id: entry.id,
      kind: entry.kind,
      decision: entry.decision,
      title: this.labelForTimelineEntry(entry),
      badges,
      inputRef: entry.inputRef,
      outputRef: entry.outputRef,
      timestampUtc: entry.timestampUtc,
      expandedExtras: expandedExtras.length > 0 ? expandedExtras : undefined,
      tokenUsage: tokenUsage
        ? {
            callCount: tokenUsage.callCount,
            totals: tokenUsage.totals,
            byProviderModel: tokenUsage.byProviderModel,
          }
        : null,
    };
  }

  private labelForSubflowNode(node: WorkflowNode): string {
    const key = node.subflowKey?.trim();
    return key ? `Subflow — ${key}` : `Subflow ${node.id.substring(0, 8)}`;
  }

  private loadWorkflowForTrace(detail: TraceDetail): void {
    if (
      this.loadedWorkflowKey === detail.workflowKey &&
      this.loadedWorkflowVersion === detail.workflowVersion
    ) {
      return;
    }
    this.workflowsApi.getVersion(detail.workflowKey, detail.workflowVersion).pipe(
      takeUntilDestroyed(this.destroyRef)
    ).subscribe({
      next: wf => {
        this.loadedWorkflowKey = detail.workflowKey;
        this.loadedWorkflowVersion = detail.workflowVersion;
        this.workflow.set(wf);
      }
    });
  }

  private appendEvent(evt: TraceStreamEvent): void {
    // Token Usage Tracking [Slice 6]: TokenUsageRecorded events are not timeline
    // rows — they go to the token-usage panel for incremental rollup updates.
    if (evt.kind === 'TokenUsageRecorded') {
      const payload = evt.tokenUsage;
      if (payload) {
        this.tokenUsagePanel()?.appendStreamEvent(payload, evt.timestampUtc);
      }
      return;
    }

    const entry: TimelineEntry = {
      id: `${evt.roundId}-${evt.agentKey}-${evt.kind}-${evt.timestampUtc}`,
      kind: evt.kind,
      agentKey: evt.agentKey,
      agentVersion: evt.agentVersion,
      decision: evt.decision,
      decisionPayload: evt.decisionPayload,
      timestampUtc: evt.timestampUtc,
      inputRef: evt.inputRef,
      outputRef: evt.outputRef
    };

    const existing = this.timeline();
    if (existing.some(e => e.id === entry.id)) return;
    this.timeline.set([...existing, entry]);

    if (evt.kind === 'Requested') {
      timer(400).pipe(
        takeUntilDestroyed(this.destroyRef)
      ).subscribe(() => this.reload());
    }

    if (evt.kind === 'Completed') {
      this.reload();
      timer(600).pipe(
        takeUntilDestroyed(this.destroyRef)
      ).subscribe(() => this.reload());
    }
  }

  private readNestedString(value: unknown, path: string[]): string | null {
    let current: unknown = value;
    for (const segment of path) {
      if (!current || typeof current !== 'object' || !(segment in current)) {
        return null;
      }
      current = (current as Record<string, unknown>)[segment];
    }
    return typeof current === 'string' && current.length > 0 ? current : null;
  }

  httpDiagnosticsRefForDecision(decisionPayload: unknown): string | null {
    return this.readNestedString(decisionPayload, ['payload', 'http_diagnostics_ref']);
  }

  private fileNameForArtifact(uri: string): string {
    try {
      const parsed = new URL(uri);
      const fileName = parsed.pathname.split('/').filter(Boolean).at(-1);
      return fileName || 'artifact.txt';
    } catch {
      return 'artifact.txt';
    }
  }

  private fileNameFromResponse(contentDisposition: string | null): string | null {
    if (!contentDisposition) return null;
    const match = /filename="?([^";]+)"?/i.exec(contentDisposition);
    return match?.[1] ?? null;
  }
}
