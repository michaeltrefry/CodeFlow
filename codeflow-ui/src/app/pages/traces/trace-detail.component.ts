import { Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { CommonModule, DatePipe, JsonPipe } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { Subscription, interval, retry, timer } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { TracesApi } from '../../core/traces.api';
import { WorkflowsApi } from '../../core/workflows.api';
import {
  AgentDecisionKind,
  TraceDetail,
  TraceLogicEvaluation,
  TraceStreamEvent,
  TraceSummary,
  WorkflowDetail,
  WorkflowNode
} from '../../core/models';
import { streamTrace } from '../../core/trace-stream';
import { AuthService } from '../../auth/auth.service';
import { HitlReviewComponent } from '../hitl/hitl-review.component';
import { WorkflowReadonlyCanvasComponent } from '../workflows/editor/workflow-readonly-canvas.component';

interface TimelineEntry {
  id: string;
  kind: 'Requested' | 'Completed';
  agentKey: string;
  agentVersion: number;
  nodeId?: string | null;
  decision?: AgentDecisionKind | null;
  decisionPayload?: unknown;
  timestampUtc: string;
  inputRef?: string | null;
  outputRef?: string | null;
}

interface ArtifactLoadState {
  loading: boolean;
  content?: string;
  error?: string;
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
  /** The ReviewLoop node's configured LoopDecision (default "Rejected"). Used as the badge
   *  label on intermediate rounds so the user sees the actual loop trigger, not a hardcoded
   *  "Rejected" that might not match their config. */
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
    WorkflowReadonlyCanvasComponent
  ],
  template: `
    <header class="page-header">
      <div>
        <h1>Trace</h1>
        <p class="muted monospace">{{ id() }}</p>
      </div>
      <div class="header-actions">
        @if (detail(); as d) {
          @if (d.currentState === 'Running') {
            <button class="secondary danger" (click)="terminate()" [disabled]="actionBusy()">
              {{ actionBusy() ? 'Terminating…' : 'Terminate trace' }}
            </button>
          } @else {
            <button class="secondary danger" (click)="deleteTrace()" [disabled]="actionBusy()">
              {{ actionBusy() ? 'Deleting…' : 'Delete trace' }}
            </button>
          }
        }
        <a routerLink="/traces"><button class="secondary" [disabled]="actionBusy()">Back</button></a>
      </div>
    </header>

    @if (detail(); as d) {
      <section class="card">
        <div class="row">
          <span class="tag" [class.ok]="d.currentState === 'Completed'" [class.warn]="d.currentState === 'Running'" [class.error]="d.currentState === 'Failed' || d.currentState === 'Escalated'">{{ d.currentState }}</span>
          <span class="tag">workflow: {{ d.workflowKey }} v{{ d.workflowVersion }}</span>
          <span class="tag">current: {{ d.currentAgentKey }}</span>
          <span class="tag">round: {{ d.roundCount }}</span>
        </div>
        <div class="muted small" style="margin-top: 0.5rem;">
          created {{ d.createdAtUtc | date:'medium' }} &middot; updated {{ d.updatedAtUtc | date:'medium' }}
        </div>
        @if (d.failureReason) {
          <div class="failure-reason">
            <strong>Failure:</strong> {{ d.failureReason }}
            @if (failureHttpDiagnosticsRef(); as diagnosticsRef) {
              <div class="failure-links">
                <a href="" (click)="downloadArtifact($event, diagnosticsRef)">Download HTTP diagnostics</a>
              </div>
            }
          </div>
        }
      </section>

      @if (d.pendingHitl.length > 0) {
        <section class="card">
          <h3>Awaiting human review</h3>
          @for (group of pendingHitlGroups(); track group.traceId) {
            @if (group.isSubflow) {
              <div class="hitl-group-header">
                <span class="tag small subflow">Subflow</span>
                <span class="mono small">{{ group.subflowPathLabel }}</span>
                @if (group.traceId !== d.traceId) {
                  <a class="small"
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
        </section>
      }

      @if (workflow()) {
        <section class="card">
          <h3>Path through the workflow</h3>
          <p class="muted small">Nodes that executed during this trace are highlighted; the rest are dimmed.</p>
          <div class="graph-host">
            <cf-workflow-readonly-canvas
              [workflow]="workflow()"
              [highlightedNodeIds]="highlightedNodeIds()"></cf-workflow-readonly-canvas>
          </div>
        </section>
      }

      @if (d.logicEvaluations.length > 0) {
        <section class="card">
          <h3>Script evaluations</h3>
          <p class="muted xsmall">
            Includes both Logic node evaluations and agent/HITL-attached routing scripts.
          </p>
          <table class="logic-table">
            <thead><tr><th>Node</th><th>Port chosen</th><th>Duration</th><th>Outcome</th><th>Logs</th></tr></thead>
            <tbody>
              @for (eval of d.logicEvaluations; track eval.recordedAtUtc) {
                <tr>
                  <td class="mono small">{{ labelForNode(eval.nodeId) }}</td>
                  <td>
                    @if (eval.outputPortName) {
                      <span class="tag accent">{{ eval.outputPortName }}</span>
                    } @else {
                      <span class="muted small">—</span>
                    }
                  </td>
                  <td class="muted small">{{ eval.duration }}</td>
                  <td>
                    @if (eval.failureKind) {
                      <span class="tag error">{{ eval.failureKind }}</span>
                      @if (eval.failureMessage) {
                        <div class="muted xsmall">{{ eval.failureMessage }}</div>
                      }
                    } @else {
                      <span class="tag ok">ok</span>
                    }
                  </td>
                  <td>
                    @if (eval.logs.length === 0) {
                      <span class="muted small">—</span>
                    } @else {
                      <ul class="log-list">
                        @for (log of eval.logs; track $index) {
                          <li class="mono xsmall">{{ log }}</li>
                        }
                      </ul>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </section>
      }

      @if (reviewLoopGroups().length > 0) {
        <section class="card">
          <h3>Review loops</h3>
          <p class="muted xsmall">
            Each round is a separate child saga. Rounds before the last one returned the
            configured loop-decision port (that's what triggered the next iteration). The
            last round shows the outcome — see its trace for the terminal decision.
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
                    <a [routerLink]="['/traces', round.traceId]" class="small">
                      Round {{ round.round }} of {{ round.maxRounds }} ↗
                    </a>
                    <span class="tag xsmall">{{ round.currentState }}</span>
                    @if (!round.isLastRoundSeen) {
                      <span class="tag xsmall rejected"
                            [title]="'Child returned ' + group.loopDecision + ', triggering the next round'">{{ group.loopDecision }}</span>
                    }
                  </li>
                }
              </ul>
            </div>
          }
        </section>
      }

      <section class="card">
        <h3>Timeline</h3>
        <ul class="timeline">
          @for (entry of timeline(); track entry.id) {
            <li [class.completed]="entry.kind === 'Completed'">
              <span class="timeline-dot"></span>
              <div class="timeline-body">
                <button type="button" class="timeline-toggle" (click)="toggleEntry(entry)"
                        [disabled]="!entry.inputRef && !entry.outputRef"
                        [attr.aria-expanded]="expandedEntries().has(entry.id)">
                  <div class="timeline-header">
                    <strong>{{ labelForTimelineEntry(entry) }}</strong>
                    <span class="muted small">v{{ entry.agentVersion }} &middot; {{ entry.timestampUtc | date:'mediumTime' }}</span>
                  </div>
                  <div>
                    <span class="tag" [class.accent]="entry.kind === 'Requested'" [class.ok]="entry.decision === 'Completed'" [class.error]="entry.decision === 'Failed' || entry.decision === 'Rejected'">
                      {{ entry.kind }}{{ entry.decision ? ': ' + entry.decision : '' }}
                    </span>
                    @if (entry.inputRef || entry.outputRef) {
                      <span class="caret">{{ expandedEntries().has(entry.id) ? '▾' : '▸' }}</span>
                    }
                  </div>
                </button>
                @if (expandedEntries().has(entry.id)) {
                  <div class="timeline-expand">
                    @if (entry.inputRef) {
                      <div class="artifact-block">
                        <h4>Input</h4>
                        <p class="artifact-link-row">
                          <a href="" (click)="downloadArtifact($event, entry.inputRef)">Download input artifact</a>
                        </p>
                        @if (artifactState(entry.inputRef); as state) {
                          @if (state.loading) { <p class="muted small">Loading&hellip;</p> }
                          @else if (state.error) { <p class="muted small error">{{ state.error }}</p> }
                          @else if (state.content !== undefined) { <pre class="monospace">{{ state.content }}</pre> }
                        }
                      </div>
                    }
                    @if (entry.outputRef) {
                      <div class="artifact-block">
                        <h4>Output</h4>
                        @if (httpDiagnosticsRefForDecision(entry.decisionPayload); as diagnosticsRef) {
                          <p class="artifact-link-row">
                            <a href="" (click)="downloadArtifact($event, diagnosticsRef)">Download HTTP diagnostics</a>
                          </p>
                        }
                        @if (artifactState(entry.outputRef); as state) {
                          @if (state.loading) { <p class="muted small">Loading&hellip;</p> }
                          @else if (state.error) { <p class="muted small error">{{ state.error }}</p> }
                          @else if (state.content !== undefined) { <pre class="monospace">{{ state.content }}</pre> }
                        }
                      </div>
                    }
                  </div>
                }
              </div>
            </li>
          }
        </ul>
      </section>

      <section class="card">
        <h3>Pinned agent versions</h3>
        <pre class="monospace">{{ d.pinnedAgentVersions | json }}</pre>
      </section>

      <section class="card">
        <h3>Context inputs</h3>
        <p class="muted small">Current saga context available to workflow scripts and agent templates.</p>
        <pre class="monospace">{{ d.contextInputs | json }}</pre>
      </section>

      @if (actionError()) {
        <p class="tag error">{{ actionError() }}</p>
      }
    } @else {
      <p>Loading trace&hellip;</p>
    }
  `,
  styles: [`
    .page-header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      margin-bottom: 1.5rem;
    }
    .header-actions {
      display: flex;
      gap: 0.75rem;
      align-items: center;
    }
    .muted { color: var(--color-muted); }
    .small { font-size: 0.8rem; }
    .xsmall { font-size: 0.72rem; }
    .mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
    button.secondary.danger {
      background: rgba(248, 81, 73, 0.12);
      border: 1px solid #f87171;
      color: #fecaca;
    }
    button.secondary.danger:hover {
      background: rgba(248, 81, 73, 0.22);
      color: #fff5f5;
    }
    .graph-host { height: 460px; margin-top: 0.5rem; }
    .hitl-group-header {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      padding: 0.5rem 0;
      margin-top: 0.75rem;
      border-top: 1px solid var(--color-border);
    }
    .hitl-group-header:first-child { border-top: none; margin-top: 0; padding-top: 0; }
    .tag.subflow { background: rgba(46, 163, 242, 0.2); color: #2ea3f2; padding: 0.15rem 0.4rem; border-radius: 3px; }
    .tag.rejected { background: rgba(248, 81, 73, 0.18); color: #fca5a5; padding: 0.15rem 0.4rem; border-radius: 3px; }
    .review-loop-group { padding: 0.5rem 0; }
    .review-loop-group + .review-loop-group { border-top: 1px solid rgba(255, 255, 255, 0.06); margin-top: 0.5rem; padding-top: 0.75rem; }
    .review-loop-rounds { list-style: none; padding: 0.25rem 0 0 0; margin: 0; display: flex; flex-direction: column; gap: 0.25rem; }
    .review-loop-rounds li { display: flex; gap: 0.5rem; align-items: center; }
    .logic-table { width: 100%; border-collapse: collapse; }
    .logic-table th, .logic-table td { padding: 0.4rem; border-bottom: 1px solid var(--color-border); text-align: left; vertical-align: top; }
    .logic-table th { color: var(--color-muted); text-transform: uppercase; font-size: 0.75rem; }
    .log-list { margin: 0; padding-left: 1rem; list-style: disc; }
    .timeline {
      list-style: none;
      padding: 0;
      margin: 0;
    }
    .timeline li {
      display: grid;
      grid-template-columns: 12px 1fr;
      gap: 0.75rem;
      padding: 0.5rem 0;
      border-left: 2px solid var(--color-border);
      padding-left: 1rem;
      position: relative;
    }
    .timeline-dot {
      width: 10px;
      height: 10px;
      background: var(--color-accent);
      border-radius: 50%;
      margin-top: 0.35rem;
    }
    .timeline li.completed .timeline-dot {
      background: var(--color-ok);
    }
    .timeline-header {
      display: flex;
      justify-content: space-between;
      align-items: baseline;
    }
    pre.monospace {
      white-space: pre-wrap;
      word-break: break-word;
      background: var(--color-surface-alt);
      padding: 0.75rem;
      border-radius: 4px;
    }
    .failure-reason {
      margin-top: 0.75rem;
      padding: 0.75rem;
      border-left: 3px solid var(--color-error, #c0392b);
      background: var(--color-surface-alt);
      border-radius: 4px;
    }
    .failure-links {
      margin-top: 0.5rem;
      font-size: 0.9rem;
    }
    .timeline-body {
      display: flex;
      flex-direction: column;
      gap: 0.25rem;
    }
    .timeline-toggle {
      all: unset;
      display: flex;
      flex-direction: column;
      gap: 0.25rem;
      width: 100%;
      cursor: pointer;
      padding: 0.25rem 0;
    }
    .timeline-toggle:focus-visible {
      outline: 2px solid var(--color-accent);
      outline-offset: 2px;
      border-radius: 2px;
    }
    .timeline-toggle:disabled {
      cursor: default;
    }
    .caret {
      margin-left: 0.5rem;
      color: var(--color-muted);
    }
    .timeline-expand {
      margin-top: 0.5rem;
      display: flex;
      flex-direction: column;
      gap: 0.75rem;
    }
    .artifact-block h4 {
      margin: 0 0 0.25rem;
      font-size: 0.85rem;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: var(--color-muted);
    }
    .artifact-link-row {
      margin: 0 0 0.5rem;
      font-size: 0.9rem;
    }
    .artifact-block .error {
      color: var(--color-error, #c0392b);
    }
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
  readonly expandedEntries = signal<Set<string>>(new Set());
  readonly actionBusy = signal(false);
  readonly actionError = signal<string | null>(null);
  readonly childTraces = signal<TraceSummary[]>([]);
  private readonly artifacts = signal<Map<string, ArtifactLoadState>>(new Map());

  /**
   * For each ReviewLoop node in the current workflow, group the child sagas it spawned (ordered
   * by their 1-indexed parent_review_round) so the trace detail can show "Round N of M" with
   * deep links into each round's child trace. The last round in the list is the outcome round;
   * any earlier round necessarily returned Rejected (that's what caused the next iteration), so
   * we badge it accordingly.
   */
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
    return ids.size > 0 ? Array.from(ids) : [];
  });

  /**
   * Groups pending HITL tasks by the trace that actually owns them. Tasks owned by the current
   * trace (or with no origin/path metadata) go into a single "root" group that renders as a flat
   * list — matching the pre-subflow UI. Tasks from descendant subflows are grouped together
   * under a header showing the subflow path and a deep link to the owning trace.
   */
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

  artifactState(uri: string | null | undefined): ArtifactLoadState | undefined {
    if (!uri) return undefined;
    return this.artifacts().get(uri);
  }

  toggleEntry(entry: TimelineEntry): void {
    const expanded = new Set(this.expandedEntries());
    if (expanded.has(entry.id)) {
      expanded.delete(entry.id);
    } else {
      expanded.add(entry.id);
      if (entry.inputRef) this.loadArtifact(entry.inputRef);
      if (entry.outputRef) this.loadArtifact(entry.outputRef);
    }
    this.expandedEntries.set(expanded);
  }

  private loadArtifact(uri: string): void {
    const existing = this.artifacts().get(uri);
    if (existing && (existing.content !== undefined || existing.loading)) return;

    const next = new Map(this.artifacts());
    next.set(uri, { loading: true });
    this.artifacts.set(next);

    this.api.getArtifact(this.id(), uri).subscribe({
      next: content => {
        const m = new Map(this.artifacts());
        m.set(uri, { loading: false, content });
        this.artifacts.set(m);
      },
      error: err => {
        const m = new Map(this.artifacts());
        m.set(uri, { loading: false, error: err?.message ?? 'Failed to load artifact.' });
        this.artifacts.set(m);
      }
    });
  }

  downloadArtifact(event: Event, uri: string): void {
    event.preventDefault();

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

  ngOnInit(): void {
    this.reload();

    this.streamSub = streamTrace(this.id(), this.auth).subscribe({
      next: evt => this.appendEvent(evt)
    });

    // The per-trace SSE stream only surfaces events for *this* trace id. With subflows and
    // ReviewLoops, HITL tasks and agent invocations fire on child saga trace ids — the parent
    // client never sees them via SSE. Poll the aggregated trace detail every 3s while the
    // parent is Running so descendant HITL tasks show up without a manual refresh. Stops
    // polling once the trace reaches a terminal state.
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
        const baseline: TimelineEntry[] = detail.decisions.map((d, i) => ({
          id: `init-${i}`,
          kind: 'Completed',
          agentKey: d.agentKey,
          agentVersion: d.agentVersion,
          nodeId: d.nodeId,
          decision: d.decision,
          decisionPayload: d.decisionPayload,
          timestampUtc: d.recordedAtUtc,
          inputRef: d.inputRef,
          outputRef: d.outputRef
        }));
        this.timeline.set(baseline);
        this.loadWorkflowForTrace(detail);
        this.loadChildTracesFor(detail.traceId);
      }
    });
  }

  private loadChildTracesFor(traceId: string): void {
    // ReviewLoop iterations produce child sagas keyed by parent_trace_id + parent_node_id +
    // parent_review_round. We list and filter client-side — good enough until the trace count
    // justifies a server-side filter.
    this.api.list().subscribe({
      next: all => this.childTraces.set(all.filter(t => t.parentTraceId === traceId)),
      error: () => this.childTraces.set([])
    });
  }

  terminate(): void {
    const detail = this.detail();
    if (!detail || detail.currentState !== 'Running') {
      return;
    }

    if (!window.confirm(`Terminate trace ${detail.traceId}?`)) {
      return;
    }

    this.actionBusy.set(true);
    this.actionError.set(null);
    this.api.terminate(detail.traceId).subscribe({
      next: () => {
        this.actionBusy.set(false);
        this.reload();
      },
      error: err => {
        this.actionBusy.set(false);
        this.actionError.set(err?.error?.error ?? err?.message ?? 'Failed to terminate trace');
      }
    });
  }

  deleteTrace(): void {
    const detail = this.detail();
    if (!detail || detail.currentState === 'Running') {
      return;
    }

    if (!window.confirm(`Delete trace ${detail.traceId}? This removes its history.`)) {
      return;
    }

    this.actionBusy.set(true);
    this.actionError.set(null);
    this.api.delete(detail.traceId).subscribe({
      next: () => {
        this.actionBusy.set(false);
        this.router.navigate(['/traces']);
      },
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
    if (explicit) {
      return explicit;
    }

    if (entry.nodeId) {
      return this.labelForNode(entry.nodeId);
    }

    return 'workflow step';
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
    if (existing.some(e => e.id === entry.id)) { return; }
    this.timeline.set([...existing, entry]);

    if (evt.kind === 'Requested') {
      // HITL tasks are created after the invoke request is observed, so schedule
      // a follow-up reload to surface newly pending human-review work.
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
    if (!contentDisposition) {
      return null;
    }

    const match = /filename=\"?([^\";]+)\"?/i.exec(contentDisposition);
    return match?.[1] ?? null;
  }
}
