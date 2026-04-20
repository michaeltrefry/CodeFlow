import { Component, inject, input, signal, OnInit, OnDestroy } from '@angular/core';
import { DatePipe, JsonPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
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
  outputRef?: string | null;
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
              <div>
                <div class="timeline-header">
                  <strong>{{ entry.agentKey }}</strong>
                  <span class="muted small">v{{ entry.agentVersion }} &middot; {{ entry.timestampUtc | date:'mediumTime' }}</span>
                </div>
                <div>
                  <span class="tag" [class.accent]="entry.kind === 'Requested'" [class.ok]="entry.decision === 'Completed'" [class.error]="entry.decision === 'Failed' || entry.decision === 'Rejected'">
                    {{ entry.kind }}{{ entry.decision ? ': ' + entry.decision : '' }}
                  </span>
                </div>
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
  `]
})
export class TraceDetailComponent implements OnInit, OnDestroy {
  private readonly api = inject(TracesApi);

  readonly id = input.required<string>();
  readonly detail = signal<TraceDetail | null>(null);
  readonly timeline = signal<TimelineEntry[]>([]);

  private streamSub?: Subscription;

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
    this.api.get(this.id()).subscribe({
      next: detail => {
        this.detail.set(detail);
        const baseline: TimelineEntry[] = detail.decisions.map((d, i) => ({
          id: `init-${i}`,
          kind: 'Completed',
          agentKey: d.agentKey,
          agentVersion: d.agentVersion,
          decision: d.decision,
          timestampUtc: d.recordedAtUtc
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
      outputRef: evt.outputRef
    };

    const existing = this.timeline();
    if (existing.some(e => e.id === entry.id)) { return; }
    this.timeline.set([...existing, entry]);

    if (evt.kind === 'Completed') {
      this.reload();
    }
  }
}
