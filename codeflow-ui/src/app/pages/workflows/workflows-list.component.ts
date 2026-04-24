import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { WorkflowsApi } from '../../core/workflows.api';
import { WorkflowSummary } from '../../core/models';
import { PageHeaderComponent } from '../../ui/page-header.component';
import { ButtonComponent } from '../../ui/button.component';
import { ChipComponent } from '../../ui/chip.component';

@Component({
  selector: 'cf-workflows-list',
  standalone: true,
  imports: [RouterLink, DatePipe, PageHeaderComponent, ButtonComponent, ChipComponent],
  template: `
    <div class="page">
      <cf-page-header
        title="Workflows"
        subtitle="Versioned graphs of agents, logic, and human checkpoints.">
        <a routerLink="/workflows/new">
          <button type="button" cf-button variant="primary" icon="plus">New workflow</button>
        </a>
      </cf-page-header>

      @if (loading()) {
        <div class="card"><div class="card-body muted">Loading workflows…</div></div>
      } @else if (error()) {
        <div class="card"><div class="card-body"><cf-chip variant="err" dot>{{ error() }}</cf-chip></div></div>
      } @else if (workflows().length === 0) {
        <div class="card"><div class="card-body muted">No workflows yet. Create one to get started.</div></div>
      } @else {
        <div class="card" style="overflow: hidden">
          <table class="table">
            <thead>
              <tr>
                <th>Key</th>
                <th>Name</th>
                <th>Version</th>
                <th>Nodes</th>
                <th>Edges</th>
                <th>Inputs</th>
                <th>Created</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (wf of workflows(); track wf.key) {
                <tr (click)="open(wf.key)">
                  <td class="mono" style="font-weight: 500">{{ wf.key }}</td>
                  <td>{{ wf.name }}</td>
                  <td><cf-chip mono>v{{ wf.latestVersion }}</cf-chip></td>
                  <td class="mono muted">{{ wf.nodeCount }}</td>
                  <td class="mono muted">{{ wf.edgeCount }}</td>
                  <td class="mono muted">{{ wf.inputCount }}</td>
                  <td class="muted small">{{ wf.createdAtUtc | date:'medium' }}</td>
                  <td class="actions">
                    <a [routerLink]="['/workflows', wf.key, 'edit']" (click)="$event.stopPropagation()">
                      <button type="button" cf-button size="sm">Edit</button>
                    </a>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </div>
  `,
})
export class WorkflowsListComponent {
  private readonly api = inject(WorkflowsApi);
  private readonly router = inject(Router);

  readonly workflows = signal<WorkflowSummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  constructor() {
    this.api.list().subscribe({
      next: wfs => { this.workflows.set(wfs); this.loading.set(false); },
      error: err => { this.error.set(err?.message ?? 'Failed to load'); this.loading.set(false); },
    });
  }

  open(key: string): void {
    this.router.navigate(['/workflows', key]);
  }
}
