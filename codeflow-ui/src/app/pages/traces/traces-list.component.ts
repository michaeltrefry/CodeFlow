import { Component, inject, signal } from '@angular/core';
import { DatePipe, SlicePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TracesApi } from '../../core/traces.api';
import { TraceSummary } from '../../core/models';

@Component({
  selector: 'cf-traces-list',
  standalone: true,
  imports: [RouterLink, DatePipe, SlicePipe, FormsModule],
  template: `
    <header class="page-header">
      <h1>Traces</h1>
      <a routerLink="/traces/new"><button>Submit run</button></a>
    </header>

    <section class="card bulk-actions">
      <div>
        <h3>Bulk cleanup</h3>
        <p class="muted small">Delete terminal traces that are older than a cutoff. Running traces are never included.</p>
      </div>
      <div class="bulk-controls">
        <label class="bulk-field">
          <span class="muted small">State</span>
          <select [(ngModel)]="bulkState" name="bulkState">
            @for (option of bulkStateOptions; track option.value) {
              <option [ngValue]="option.value">{{ option.label }}</option>
            }
          </select>
        </label>
        <label class="bulk-field">
          <span class="muted small">Older than</span>
          <select [(ngModel)]="bulkOlderThanDays" name="bulkOlderThanDays">
            @for (option of bulkAgeOptions; track option.days) {
              <option [ngValue]="option.days">{{ option.label }}</option>
            }
          </select>
        </label>
        <button class="secondary danger" (click)="bulkDelete()" [disabled]="bulkBusy()">
          {{ bulkBusy() ? 'Deleting…' : bulkButtonLabel() }}
        </button>
      </div>
      @if (bulkResult()) {
        <p class="muted small">{{ bulkResult() }}</p>
      }
    </section>

    @if (loading()) {
      <p>Loading traces&hellip;</p>
    } @else if (error()) {
      <p class="tag error">{{ error() }}</p>
    } @else if (traces().length === 0) {
      <p class="tag">No traces yet.</p>
    } @else {
      <table class="trace-table card">
        <thead>
          <tr><th>Trace ID</th><th>Workflow</th><th>Current agent</th><th>State</th><th>Round</th><th>Updated</th><th>Actions</th></tr>
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
              <td class="actions-cell">
                @if (trace.currentState === 'Running') {
                  <button class="secondary danger" (click)="terminate(trace); $event.stopPropagation()" [disabled]="busyTraceId() === trace.traceId">
                    {{ busyTraceId() === trace.traceId ? 'Terminating…' : 'Terminate' }}
                  </button>
                } @else {
                  <button class="secondary danger" (click)="delete(trace); $event.stopPropagation()" [disabled]="busyTraceId() === trace.traceId">
                    {{ busyTraceId() === trace.traceId ? 'Deleting…' : 'Delete' }}
                  </button>
                }
              </td>
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
    .bulk-actions {
      display: flex;
      justify-content: space-between;
      align-items: end;
      gap: 1rem;
      margin-bottom: 1rem;
    }
    .bulk-actions h3 {
      margin: 0 0 0.25rem;
    }
    .bulk-actions p {
      margin: 0;
    }
    .bulk-controls {
      display: flex;
      align-items: end;
      gap: 0.75rem;
      flex-wrap: wrap;
      justify-content: flex-end;
    }
    .bulk-field {
      display: flex;
      flex-direction: column;
      gap: 0.25rem;
      min-width: 10rem;
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
    .actions-cell {
      white-space: nowrap;
      width: 1%;
    }
    button.secondary.danger {
      background: rgba(248, 81, 73, 0.12);
      border: 1px solid #f87171;
      color: #fecaca;
    }
    button.secondary.danger:hover {
      background: rgba(248, 81, 73, 0.22);
      color: #fff5f5;
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
  readonly busyTraceId = signal<string | null>(null);
  readonly bulkBusy = signal(false);
  readonly bulkResult = signal<string | null>(null);

  readonly bulkStateOptions = BULK_STATE_OPTIONS;
  readonly bulkAgeOptions = BULK_AGE_OPTIONS;
  readonly bulkState = signal<string | null>('Completed');
  readonly bulkOlderThanDays = signal(7);

  constructor() {
    this.reload();
  }

  terminate(trace: TraceSummary): void {
    if (!window.confirm(`Terminate trace ${trace.traceId}?`)) {
      return;
    }

    this.busyTraceId.set(trace.traceId);
    this.error.set(null);
    this.api.terminate(trace.traceId).subscribe({
      next: () => {
        this.busyTraceId.set(null);
        this.reload();
      },
      error: err => {
        this.busyTraceId.set(null);
        this.error.set(err?.error?.error ?? err?.message ?? 'Failed to terminate trace');
      }
    });
  }

  delete(trace: TraceSummary): void {
    if (!window.confirm(`Delete trace ${trace.traceId}? This removes its history.`)) {
      return;
    }

    this.busyTraceId.set(trace.traceId);
    this.error.set(null);
    this.api.delete(trace.traceId).subscribe({
      next: () => {
        this.busyTraceId.set(null);
        this.reload();
      },
      error: err => {
        this.busyTraceId.set(null);
        this.error.set(err?.error?.error ?? err?.message ?? 'Failed to delete trace');
      }
    });
  }

  bulkDelete(): void {
    const stateLabel = this.bulkStateOptions.find(option => option.value === this.bulkState())?.label ?? 'selected traces';
    const olderThanDays = this.bulkOlderThanDays();
    if (!window.confirm(`Delete ${stateLabel.toLowerCase()} older than ${olderThanDays} days? This removes their history.`)) {
      return;
    }

    this.bulkBusy.set(true);
    this.error.set(null);
    this.bulkResult.set(null);
    this.api.bulkDelete({
      state: this.bulkState(),
      olderThanDays
    }).subscribe({
      next: response => {
        this.bulkBusy.set(false);
        this.bulkResult.set(`Deleted ${response.deletedCount} trace${response.deletedCount === 1 ? '' : 's'}.`);
        this.reload();
      },
      error: err => {
        this.bulkBusy.set(false);
        this.error.set(err?.error?.error ?? err?.message ?? 'Failed to bulk delete traces');
      }
    });
  }

  bulkButtonLabel(): string {
    const stateLabel = this.bulkStateOptions.find(option => option.value === this.bulkState())?.shortLabel ?? 'terminal';
    return `Delete ${stateLabel}`;
  }

  private reload(): void {
    this.loading.set(true);
    this.api.list().subscribe({
      next: ts => { this.traces.set(ts); this.loading.set(false); },
      error: err => { this.error.set(err?.message ?? 'Failed to load'); this.loading.set(false); }
    });
  }
}

const BULK_STATE_OPTIONS = [
  { value: 'Completed', label: 'Completed traces', shortLabel: 'completed' },
  { value: 'Failed', label: 'Failed traces', shortLabel: 'failed' },
  { value: 'Escalated', label: 'Escalated traces', shortLabel: 'escalated' },
  { value: null, label: 'All terminal traces', shortLabel: 'terminal' }
];

const BULK_AGE_OPTIONS = [
  { days: 1, label: '1 day' },
  { days: 7, label: '7 days' },
  { days: 30, label: '30 days' },
  { days: 90, label: '90 days' }
];
