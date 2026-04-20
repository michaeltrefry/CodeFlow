import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { TracesApi } from '../../core/traces.api';
import { HitlTask } from '../../core/models';

@Component({
  selector: 'cf-hitl-queue',
  standalone: true,
  imports: [RouterLink, DatePipe],
  template: `
    <header class="page-header">
      <h1>HITL queue</h1>
      <button class="secondary" (click)="reload()">Refresh</button>
    </header>

    @if (loading()) {
      <p>Loading pending reviews&hellip;</p>
    } @else if (tasks().length === 0) {
      <p class="tag ok">No pending human reviews.</p>
    } @else {
      <div class="stack">
        @for (task of tasks(); track task.id) {
          <a class="card hitl-link" [routerLink]="['/traces', task.traceId]">
            <div class="row" style="justify-content: space-between;">
              <div>
                <strong>{{ task.agentKey }}</strong>
                <span class="muted small"> v{{ task.agentVersion }}</span>
              </div>
              <span class="tag warn">{{ task.state }}</span>
            </div>
            <div class="muted small">trace {{ task.traceId }}</div>
            <div class="muted small">created {{ task.createdAtUtc | date:'medium' }}</div>
            @if (task.inputPreview) {
              <p class="preview">{{ task.inputPreview }}</p>
            }
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
    .hitl-link {
      display: block;
      color: inherit;
    }
    .hitl-link:hover {
      border-color: var(--color-accent);
    }
    .muted { color: var(--color-muted); }
    .small { font-size: 0.8rem; }
    .preview {
      margin-top: 0.5rem;
      max-height: 120px;
      overflow: hidden;
      background: var(--color-surface-alt);
      padding: 0.5rem;
      border-radius: 4px;
      font-family: 'SFMono-Regular', Consolas, monospace;
      font-size: 0.85rem;
    }
  `]
})
export class HitlQueueComponent {
  private readonly api = inject(TracesApi);

  readonly tasks = signal<HitlTask[]>([]);
  readonly loading = signal(true);

  constructor() {
    this.reload();
  }

  reload(): void {
    this.loading.set(true);
    this.api.pendingHitl().subscribe({
      next: tasks => {
        this.tasks.set(tasks);
        this.loading.set(false);
      },
      error: () => this.loading.set(false)
    });
  }
}
