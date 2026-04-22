import { Component, inject, input, signal, OnInit, OnDestroy } from '@angular/core';
import { DatePipe, JsonPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Subscription, retry, timer } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { TracesApi } from '../../core/traces.api';
import { TraceDetail, TraceStreamEvent, AgentDecisionKind } from '../../core/models';
import { streamTrace } from '../../core/trace-stream';
import { HitlReviewComponent } from '../hitl/hitl-review.component';

interface TimelineEntry {
  id: string;
  kind: 'Requested' | 'Completed';
  agentKey: string;
  agentVersion: number;
  decision?: AgentDecisionKind | null;
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
  imports: [RouterLink, DatePipe, JsonPipe, HitlReviewComponent],
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
          </div>
        }
      </section>

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
    .artifact-block .error {
      color: var(--color-error, #c0392b);
    }
  `]
})
export class TraceDetailComponent implements OnInit, OnDestroy {
  private readonly api = inject(TracesApi);

  readonly id = input.required<string>();
  readonly detail = signal<TraceDetail | null>(null);
  readonly timeline = signal<TimelineEntry[]>([]);
  readonly expandedEntries = signal<Set<string>>(new Set());
  private readonly artifacts = signal<Map<string, ArtifactLoadState>>(new Map());

  private streamSub?: Subscription;

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

  ngOnInit(): void {
    this.reload();

    this.streamSub = streamTrace(this.id()).subscribe({
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
          timestampUtc: d.recordedAtUtc,
          inputRef: d.inputRef,
          outputRef: d.outputRef
        }));
        this.timeline.set(baseline);
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
}
