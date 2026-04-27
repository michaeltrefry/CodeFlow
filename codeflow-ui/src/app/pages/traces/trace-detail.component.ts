import { Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { CommonModule, DatePipe, JsonPipe } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { Subscription, interval, retry, timer } from 'rxjs';
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
import { HitlReviewComponent } from '../hitl/hitl-review.component';
import { WorkflowReadonlyCanvasComponent } from '../workflows/editor/workflow-readonly-canvas.component';
import { PageHeaderComponent } from '../../ui/page-header.component';
import { ButtonComponent } from '../../ui/button.component';
import { ChipComponent } from '../../ui/chip.component';
import { StateChipComponent } from '../../ui/state-chip.component';
import { CardComponent } from '../../ui/card.component';
import { TraceTimelineComponent } from '../../ui/trace-timeline.component';
import { TraceTimelineEvent, TraceTimelineExtraLink } from '../../ui/trace-timeline.types';
import { Observable } from 'rxjs';

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
                [highlightedNodeIds]="highlightedNodeIds()"></cf-workflow-readonly-canvas>
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

        <cf-card title="Execution timeline" flush>
          <ng-template #cardRight><cf-chip mono>{{ timeline().length }} hops</cf-chip></ng-template>
          <cf-trace-timeline
            [events]="timelineEvents()"
            [fetchArtifact]="artifactFetcher"
            (downloadRef)="onTimelineDownload($event)"></cf-trace-timeline>
        </cf-card>

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

  readonly id = input.required<string>();
  readonly detail = signal<TraceDetail | null>(null);
  readonly workflow = signal<WorkflowDetail | null>(null);
  readonly timeline = signal<TimelineEntry[]>([]);
  readonly actionBusy = signal(false);
  readonly actionError = signal<string | null>(null);
  readonly childTraces = signal<TraceSummary[]>([]);
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

  private streamSub?: Subscription;
  private pollSub?: Subscription;
  private loadedWorkflowKey: string | null = null;
  private loadedWorkflowVersion: number | null = null;

  /** Click handler for `<cf-trace-timeline>` `(downloadRef)` events — kicks off the
   *  blob download via TracesApi. The shared timeline emits the URI for both the
   *  per-row "Download input artifact" link and any extra-link (HTTP diagnostics). */
  onTimelineDownload(uri: string): void {
    this.downloadArtifact(uri);
  }

  downloadArtifact(uri: string): void {
    this.api.downloadArtifact(this.id(), uri).subscribe({
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

  ngOnInit(): void {
    this.reload();

    this.streamSub = streamTrace(this.id(), this.auth).subscribe({
      next: evt => this.appendEvent(evt)
    });

    this.pollSub = interval(3000).subscribe(() => {
      const current = this.detail();
      if (current && current.currentState === 'Running') {
        this.reload();
      }
    });
  }

  ngOnDestroy(): void {
    this.streamSub?.unsubscribe();
    this.pollSub?.unsubscribe();
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
      })
    ).subscribe({
      next: detail => {
        this.detail.set(detail);
        this.rebuildTimeline();
        this.loadWorkflowForTrace(detail);
        this.loadDescendantTracesFor(detail.traceId);
      }
    });
  }

  /** Parent-of mapping for every trace returned by `api.list()` — covers the full
   *  descendant tree, not just direct children. Rebuilt every reload. */
  private readonly parentByTraceId = signal<Map<string, string>>(new Map());

  /** Load the full descendant tree (children + grandchildren + ...) so the timeline can
   *  merge their decisions in chronological order. Without this, the parent's timeline
   *  appears stuck while a Subflow/ReviewLoop is mid-execution because the parent saga
   *  doesn't record any decision until the child returns. */
  private loadDescendantTracesFor(rootTraceId: string): void {
    this.api.list().subscribe({
      next: all => {
        const directChildren = all.filter(t => t.parentTraceId === rootTraceId);
        this.childTraces.set(directChildren);

        const parentMap = new Map<string, string>();
        for (const t of all) {
          if (t.parentTraceId) parentMap.set(t.traceId, t.parentTraceId);
        }
        this.parentByTraceId.set(parentMap);

        const descendants = this.collectDescendants(rootTraceId, all);
        if (descendants.length === 0) {
          this.descendantDetails.set(new Map());
          this.rebuildTimeline();
          return;
        }

        // Fetch each descendant's detail in parallel; rebuild the timeline once all
        // arrive (or whichever ones succeed).
        let pending = descendants.length;
        const next = new Map<string, TraceDetail>();
        for (const summary of descendants) {
          this.api.get(summary.traceId).subscribe({
            next: childDetail => {
              next.set(summary.traceId, childDetail);
              if (--pending === 0) {
                this.descendantDetails.set(next);
                this.rebuildTimeline();
              }
            },
            error: () => {
              if (--pending === 0) {
                this.descendantDetails.set(next);
                this.rebuildTimeline();
              }
            }
          });
        }
      },
      error: () => {
        this.childTraces.set([]);
        this.descendantDetails.set(new Map());
        this.rebuildTimeline();
      }
    });
  }

  private collectDescendants(rootId: string, all: TraceSummary[]): TraceSummary[] {
    const childrenByParent = new Map<string, TraceSummary[]>();
    for (const t of all) {
      if (!t.parentTraceId) continue;
      const list = childrenByParent.get(t.parentTraceId) ?? [];
      list.push(t);
      childrenByParent.set(t.parentTraceId, list);
    }

    const out: TraceSummary[] = [];
    const stack = [rootId];
    while (stack.length > 0) {
      const id = stack.pop()!;
      const kids = childrenByParent.get(id) ?? [];
      for (const kid of kids) {
        out.push(kid);
        stack.push(kid.traceId);
      }
    }
    return out;
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
          originTraceId: detail.traceId
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

  terminate(): void {
    const detail = this.detail();
    if (!detail || detail.currentState !== 'Running') return;
    if (!window.confirm(`Terminate trace ${detail.traceId}?`)) return;

    this.actionBusy.set(true);
    this.actionError.set(null);
    this.api.terminate(detail.traceId).subscribe({
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
    this.api.delete(detail.traceId).subscribe({
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
    const badges = [
      { label: `v${entry.agentVersion}`, mono: true },
    ];
    if (entry.originPath) {
      badges.push({ label: `via ${entry.originPath}`, mono: true });
    }

    const expandedExtras: TraceTimelineExtraLink[] = [];
    const httpDiagnosticsRef = this.httpDiagnosticsRefForDecision(entry.decisionPayload);
    if (httpDiagnosticsRef) {
      expandedExtras.push({ label: 'Download HTTP diagnostics', ref: httpDiagnosticsRef });
    }

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
    this.workflowsApi.getVersion(detail.workflowKey, detail.workflowVersion).subscribe({
      next: wf => {
        this.loadedWorkflowKey = detail.workflowKey;
        this.loadedWorkflowVersion = detail.workflowVersion;
        this.workflow.set(wf);
      }
    });
  }

  private appendEvent(evt: TraceStreamEvent): void {
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
      setTimeout(() => this.reload(), 400);
    }

    if (evt.kind === 'Completed') {
      this.reload();
      setTimeout(() => this.reload(), 600);
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
