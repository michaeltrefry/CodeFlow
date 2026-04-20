import { Component, inject, signal } from '@angular/core';
import { DatePipe, SlicePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { TracesApi } from '../../core/traces.api';
import { TraceSummary } from '../../core/models';

@Component({
  selector: 'cf-traces-list',
  standalone: true,
  imports: [RouterLink, DatePipe, SlicePipe],
  template: `
    <header class="page-header">
      <h1>Traces</h1>
      <a routerLink="/traces/new"><button>Submit run</button></a>
    </header>

    @if (loading()) {
      <p>Loading traces&hellip;</p>
    } @else if (error()) {
      <p class="tag error">{{ error() }}</p>
    } @else if (traces().length === 0) {
      <p class="tag">No traces yet.</p>
    } @else {
      <table class="trace-table card">
        <thead>
          <tr><th>Trace ID</th><th>Workflow</th><th>Current agent</th><th>State</th><th>Round</th><th>Updated</th></tr>
        </thead>
        <tbody>
          @for (trace of traces(); track trace.traceId) {
            <tr>
              <td><a [routerLink]="['/traces', trace.traceId]" class="monospace">{{ trace.traceId | slice:0:8 }}</a></td>
              <td>{{ trace.workflowKey }} <span class="muted small">v{{ trace.workflowVersion }}</span></td>
              <td>{{ trace.currentAgentKey }}</td>
              <td><span class="tag" [class.ok]="trace.currentState === 'Completed'" [class.warn]="trace.currentState === 'Running'" [class.error]="trace.currentState === 'Failed' || trace.currentState === 'Escalated'">{{ trace.currentState }}</span></td>
              <td>{{ trace.roundCount }}</td>
              <td class="muted small">{{ trace.updatedAtUtc | date:'medium' }}</td>
            </tr>
          }
        </tbody>
      </table>
    }
  `,
  styles: [`
    .page-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 1.5rem;
    }
    table.trace-table {
      width: 100%;
      border-collapse: collapse;
    }
    table.trace-table th, table.trace-table td {
      padding: 0.55rem 0.75rem;
      border-bottom: 1px solid var(--color-border);
      text-align: left;
    }
    table.trace-table th {
      color: var(--color-muted);
      text-transform: uppercase;
      font-size: 0.75rem;
      letter-spacing: 0.05em;
    }
    .muted { color: var(--color-muted); }
    .small { font-size: 0.8rem; }
  `]
})
export class TracesListComponent {
  private readonly api = inject(TracesApi);

  readonly traces = signal<TraceSummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  constructor() {
    this.api.list().subscribe({
      next: ts => { this.traces.set(ts); this.loading.set(false); },
      error: err => { this.error.set(err?.message ?? 'Failed to load'); this.loading.set(false); }
    });
  }
}
