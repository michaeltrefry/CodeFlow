import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { WorkflowsApi } from '../../core/workflows.api';
import { WorkflowSummary } from '../../core/models';

@Component({
  selector: 'cf-workflows-list',
  standalone: true,
  imports: [RouterLink, DatePipe],
  template: `
    <header class="page-header">
      <h1>Workflows</h1>
      <a routerLink="/workflows/new"><button>New workflow</button></a>
    </header>

    @if (loading()) {
      <p>Loading workflows&hellip;</p>
    } @else if (error()) {
      <p class="tag error">{{ error() }}</p>
    } @else if (workflows().length === 0) {
      <p class="tag">No workflows yet. Create one to get started.</p>
    } @else {
      <div class="workflow-grid">
        @for (wf of workflows(); track wf.key) {
          <a class="card workflow-card" [routerLink]="['/workflows', wf.key]">
            <div class="row">
              <span class="workflow-name">{{ wf.name }}</span>
              <span class="tag accent">v{{ wf.latestVersion }}</span>
            </div>
            <div class="workflow-key muted">{{ wf.key }}</div>
            <div class="workflow-meta">
              <span class="tag">{{ wf.nodeCount }} nodes</span>
              <span class="tag">{{ wf.edgeCount }} edges</span>
              @if (wf.inputCount > 0) {
                <span class="tag">{{ wf.inputCount }} inputs</span>
              }
            </div>
            <div class="muted small">created {{ wf.createdAtUtc | date:'medium' }}</div>
          </a>
        }
      </div>
    }
  `,
  styles: [`
    .page-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 1.5rem;
    }
    .workflow-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
      gap: 1rem;
    }
    .workflow-card {
      display: block;
      color: inherit;
    }
    .workflow-card:hover {
      border-color: var(--color-accent);
    }
    .workflow-name {
      font-size: 1.05rem;
      font-weight: 600;
      flex: 1;
    }
    .workflow-key {
      font-size: 0.85rem;
      margin-bottom: 0.5rem;
    }
    .workflow-meta {
      display: flex;
      gap: 0.4rem;
      flex-wrap: wrap;
      margin-bottom: 0.5rem;
    }
    .muted { color: var(--color-muted); }
    .small { font-size: 0.8rem; }
  `]
})
export class WorkflowsListComponent {
  private readonly api = inject(WorkflowsApi);

  readonly workflows = signal<WorkflowSummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  constructor() {
    this.api.list().subscribe({
      next: wfs => { this.workflows.set(wfs); this.loading.set(false); },
      error: err => { this.error.set(err?.message ?? 'Failed to load'); this.loading.set(false); }
    });
  }
}
