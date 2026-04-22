import { Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { CommonModule, DatePipe, JsonPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Subscription, retry, timer } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { TracesApi } from '../../core/traces.api';
import { WorkflowsApi } from '../../core/workflows.api';
import {
  AgentDecisionKind,
  TraceDetail,
  TraceLogicEvaluation,
  TraceStreamEvent,
  WorkflowDetail
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
      <a routerLink="/traces"><button class="secondary">Back</button></a>
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

      @if (d.pendingHitl.length > 0) {
        <section class="card">
          <h3>Awaiting human review</h3>
          @for (task of d.pendingHitl; track task.id) {
            <cf-hitl-review [task]="task" (decided)="reload()" />
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
                    <strong>{{ entry.agentKey }}</strong>
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
    .muted { color: var(--color-muted); }
    .small { font-size: 0.8rem; }
    .xsmall { font-size: 0.72rem; }
    .mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
    .graph-host { height: 460px; margin-top: 0.5rem; }
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

  readonly id = input.required<string>();
  readonly detail = signal<TraceDetail | null>(null);
  readonly workflow = signal<WorkflowDetail | null>(null);
  readonly timeline = signal<TimelineEntry[]>([]);
  readonly expandedEntries = signal<Set<string>>(new Set());
  private readonly artifacts = signal<Map<string, ArtifactLoadState>>(new Map());

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
  }

  ngOnDestroy(): void {
    this.streamSub?.unsubscribe();
  }

  reload(): void {
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
          decision: d.decision,
          decisionPayload: d.decisionPayload,
          timestampUtc: d.recordedAtUtc,
          inputRef: d.inputRef,
          outputRef: d.outputRef
        }));
        this.timeline.set(baseline);
        this.loadWorkflowForTrace(detail);
      }
    });
  }

  labelForNode(nodeId: string): string {
    const wf = this.workflow();
    if (!wf) return nodeId;
    const node = wf.nodes.find(n => n.id === nodeId);
    if (!node) return nodeId;
    if (node.agentKey) return `${node.agentKey} (${node.kind})`;
    return `${node.kind} ${nodeId.substring(0, 8)}`;
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
