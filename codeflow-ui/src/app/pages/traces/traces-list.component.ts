import { Component, computed, inject, signal } from '@angular/core';
import { DatePipe, SlicePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { TracesApi } from '../../core/traces.api';
import { useAsyncList } from '../../core/async-state';
import { TraceSummary } from '../../core/models';
import { PageHeaderComponent } from '../../ui/page-header.component';
import { ButtonComponent } from '../../ui/button.component';
import { ChipComponent } from '../../ui/chip.component';
import { StateChipComponent } from '../../ui/state-chip.component';
import { SegmentedComponent, SegmentedOption } from '../../ui/segmented.component';

type StateFilter = 'all' | 'running' | 'terminal';

const TERMINAL_STATES = new Set(['Completed', 'Failed', 'Escalated']);

const BULK_STATE_OPTIONS = [
  { value: 'Completed', label: 'Completed traces', shortLabel: 'completed' },
  { value: 'Failed', label: 'Failed traces', shortLabel: 'failed' },
  { value: 'Escalated', label: 'Escalated traces', shortLabel: 'escalated' },
  { value: '', label: 'All terminal traces', shortLabel: 'terminal' },
];

const BULK_AGE_OPTIONS = [
  { days: 1, label: '1 day' },
  { days: 7, label: '7 days' },
  { days: 30, label: '30 days' },
  { days: 90, label: '90 days' },
];

const FILTER_OPTIONS: SegmentedOption[] = [
  { value: 'all', label: 'All' },
  { value: 'running', label: 'Running' },
  { value: 'terminal', label: 'Terminal' },
];

@Component({
  selector: 'cf-traces-list',
  standalone: true,
  imports: [
    RouterLink, DatePipe, SlicePipe, FormsModule,
    PageHeaderComponent, ButtonComponent, ChipComponent,
    StateChipComponent, SegmentedComponent,
  ],
  template: `
    <div class="page">
      <cf-page-header
        title="Traces"
        subtitle="Every workflow run, streamed in real time. Running traces update live.">
        <button type="button" cf-button variant="ghost" icon="refresh" (click)="reload()">Refresh</button>
        <button type="button" cf-button variant="primary" icon="plus" (click)="newTrace()">Submit run</button>
      </cf-page-header>

      <div class="bulk-panel">
        <div>
          <div class="label">Bulk cleanup</div>
          <div class="muted">Delete terminal traces older than a cutoff. Running traces are always excluded.</div>
        </div>
        <div class="bulk-controls">
          <div class="bulk-field">
            <span class="field-label">State</span>
            <select class="select" [(ngModel)]="bulkStateRaw" name="bulkState">
              @for (option of bulkStateOptions; track option.value) {
                <option [ngValue]="option.value">{{ option.label }}</option>
              }
            </select>
          </div>
          <div class="bulk-field">
            <span class="field-label">Older than</span>
            <select class="select" [(ngModel)]="bulkOlderThanDays" name="bulkOlderThanDays">
              @for (option of bulkAgeOptions; track option.days) {
                <option [ngValue]="option.days">{{ option.label }}</option>
              }
            </select>
          </div>
          <button type="button" cf-button variant="danger" icon="trash"
                  [disabled]="bulkBusy()" (click)="bulkDelete()">
            {{ bulkBusy() ? 'Deleting…' : bulkButtonLabel() }}
          </button>
        </div>
      </div>

      @if (bulkResult()) {
        <div class="muted small">{{ bulkResult() }}</div>
      }

      <div class="list-toolbar">
        <div class="list-toolbar-left">
          <cf-segmented
            [options]="filterOptions"
            [value]="stateFilter()"
            (valueChange)="stateFilter.set($any($event))">
          </cf-segmented>
          <label class="checkbox">
            <input type="checkbox"
                   [checked]="hideSubflowChildren()"
                   (change)="hideSubflowChildren.set($any($event.target).checked)">
            <span>Hide subflow children</span>
          </label>
        </div>
        <span class="muted small">{{ visibleTraces().length }} of {{ traces().length }} shown</span>
      </div>

      @if (loading()) {
        <div class="card"><div class="card-body muted">Loading traces…</div></div>
      } @else if (error()) {
        <div class="card"><div class="card-body"><cf-chip variant="err" dot>{{ error() }}</cf-chip></div></div>
      } @else if (traces().length === 0) {
        <div class="card"><div class="card-body muted">No traces yet.</div></div>
      } @else {
        <div class="card" style="overflow:hidden">
          <div class="scroll" style="max-height: calc(100vh - 390px)">
            <table class="table">
              <thead>
                <tr>
                  <th>Trace</th>
                  <th>Workflow</th>
                  <th>Current agent</th>
                  <th>State</th>
                  <th>Round</th>
                  <th>Updated</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                @for (trace of visibleTraces(); track trace.traceId) {
                  <tr (click)="openTrace(trace)">
                    <td>
                      <a [routerLink]="['/traces', trace.traceId]" class="mono-link" (click)="$event.stopPropagation()">{{ trace.traceId | slice:0:8 }}</a>
                      @if (trace.parentTraceId) {
                        <cf-chip style="margin-left: 6px">child</cf-chip>
                      }
                    </td>
                    <td>
                      <span style="font-weight: 500">{{ trace.workflowKey }}</span>
                      <span class="mono muted" style="margin-left:6px; font-size:12px">v{{ trace.workflowVersion }}</span>
                    </td>
                    <td class="mono" style="font-size: 12px">{{ trace.currentAgentKey }}</td>
                    <td><cf-state-chip [state]="trace.currentState"></cf-state-chip></td>
                    <td class="mono muted">{{ trace.roundCount }}</td>
                    <td class="muted small">{{ trace.updatedAtUtc | date:'medium' }}</td>
                    <td class="actions">
                      @if (trace.currentState === 'Running') {
                        <button type="button" cf-button variant="danger" size="sm"
                                (click)="$event.stopPropagation(); terminate(trace)"
                                [disabled]="busyTraceId() === trace.traceId">
                          {{ busyTraceId() === trace.traceId ? 'Terminating…' : 'Terminate' }}
                        </button>
                      } @else {
                        <button type="button" cf-button variant="ghost" size="sm" icon="trash" iconOnly
                                (click)="$event.stopPropagation(); delete(trace)"
                                [disabled]="busyTraceId() === trace.traceId"
                                [attr.aria-label]="'Delete trace ' + trace.traceId"></button>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </div>
      }
    </div>
  `,
})
export class TracesListComponent {
  private readonly api = inject(TracesApi);
  private readonly router = inject(Router);
  private readonly tracesList = useAsyncList(
    () => this.api.list(),
    { errorMessage: 'Failed to load' },
  );

  readonly traces = this.tracesList.items;
  readonly loading = this.tracesList.loading;
  readonly error = this.tracesList.error;
  readonly busyTraceId = signal<string | null>(null);
  readonly bulkBusy = signal(false);
  readonly bulkResult = signal<string | null>(null);
  readonly hideSubflowChildren = signal(false);
  readonly stateFilter = signal<StateFilter>('all');

  readonly visibleTraces = computed<TraceSummary[]>(() => {
    const all = this.traces();
    const filter = this.stateFilter();
    const hideChildren = this.hideSubflowChildren();
    return all.filter(t => {
      if (hideChildren && t.parentTraceId) return false;
      if (filter === 'running' && t.currentState !== 'Running') return false;
      if (filter === 'terminal' && !TERMINAL_STATES.has(t.currentState)) return false;
      return true;
    });
  });

  readonly bulkStateOptions = BULK_STATE_OPTIONS;
  readonly bulkAgeOptions = BULK_AGE_OPTIONS;
  readonly bulkStateRaw = signal<string>('Completed');
  readonly bulkOlderThanDays = signal(7);
  readonly filterOptions = FILTER_OPTIONS;

  constructor() {
    this.reload();
  }

  openTrace(trace: TraceSummary): void {
    this.router.navigate(['/traces', trace.traceId]);
  }

  newTrace(): void {
    this.router.navigate(['/traces/new']);
  }

  terminate(trace: TraceSummary): void {
    if (!window.confirm(`Terminate trace ${trace.traceId}?`)) return;
    this.busyTraceId.set(trace.traceId);
    this.error.set(null);
    this.api.terminate(trace.traceId).subscribe({
      next: () => { this.busyTraceId.set(null); this.reload(); },
      error: err => {
        this.busyTraceId.set(null);
        this.error.set(err?.error?.error ?? err?.message ?? 'Failed to terminate trace');
      },
    });
  }

  delete(trace: TraceSummary): void {
    if (!window.confirm(`Delete trace ${trace.traceId}? This removes its history.`)) return;
    this.busyTraceId.set(trace.traceId);
    this.error.set(null);
    this.api.delete(trace.traceId).subscribe({
      next: () => { this.busyTraceId.set(null); this.reload(); },
      error: err => {
        this.busyTraceId.set(null);
        this.error.set(err?.error?.error ?? err?.message ?? 'Failed to delete trace');
      },
    });
  }

  bulkDelete(): void {
    const raw = this.bulkStateRaw();
    const chosen = this.bulkStateOptions.find(option => option.value === raw);
    const olderThanDays = this.bulkOlderThanDays();
    const label = chosen?.label.toLowerCase() ?? 'selected traces';
    if (!window.confirm(`Delete ${label} older than ${olderThanDays} days? This removes their history.`)) return;

    this.bulkBusy.set(true);
    this.error.set(null);
    this.bulkResult.set(null);
    this.api.bulkDelete({
      state: raw === '' ? null : raw,
      olderThanDays,
    }).subscribe({
      next: response => {
        this.bulkBusy.set(false);
        this.bulkResult.set(`Deleted ${response.deletedCount} trace${response.deletedCount === 1 ? '' : 's'}.`);
        this.reload();
      },
      error: err => {
        this.bulkBusy.set(false);
        this.error.set(err?.error?.error ?? err?.message ?? 'Failed to bulk delete traces');
      },
    });
  }

  bulkButtonLabel(): string {
    const raw = this.bulkStateRaw();
    const chosen = this.bulkStateOptions.find(option => option.value === raw);
    return `Delete ${chosen?.shortLabel ?? 'terminal'}`;
  }

  reload(): void {
    this.tracesList.reload();
  }
}
