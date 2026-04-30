import { Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { relativeTime } from '../../core/format-time';
import { TracesApi } from '../../core/traces.api';
import { HitlTask } from '../../core/models';
import { PageHeaderComponent } from '../../ui/page-header.component';
import { ButtonComponent } from '../../ui/button.component';
import { ChipComponent } from '../../ui/chip.component';
import { CardComponent } from '../../ui/card.component';
import { IconComponent } from '../../ui/icon.component';

@Component({
  selector: 'cf-hitl-queue',
  standalone: true,
  imports: [
    RouterLink,
    PageHeaderComponent, ButtonComponent, ChipComponent, CardComponent, IconComponent,
  ],
  template: `
    <div class="page">
      <cf-page-header
        title="HITL queue"
        subtitle="Human-in-the-loop approval inbox. Each item pauses a workflow until decided.">
        <cf-chip variant="accent" dot>{{ pendingCount() }} pending</cf-chip>
        <button type="button" cf-button variant="ghost" icon="refresh" (click)="reload()">Refresh</button>
      </cf-page-header>

      @if (loading()) {
        <cf-card><div class="muted">Loading pending reviews…</div></cf-card>
      } @else if (tasks().length === 0) {
        <cf-card><cf-chip variant="ok" dot>No pending human reviews.</cf-chip></cf-card>
      } @else {
        <div style="display: grid; grid-template-columns: 1fr 460px; gap: 16px; align-items: flex-start">
          <div class="hitl-grid">
            @for (task of tasks(); track task.id) {
              <article class="hitl-card"
                       [attr.data-selected]="selectedId() === task.id ? 'true' : null"
                       (click)="selectedId.set(task.id)">
                <div class="hitl-ico"><cf-icon name="hitl"></cf-icon></div>
                <div class="hitl-body">
                  <div class="hitl-title">
                    <span class="mono">#{{ task.id }}</span>
                    <span>{{ task.agentKey }}</span>
                    <cf-chip mono>v{{ task.agentVersion }}</cf-chip>
                    <cf-chip mono>{{ task.roundId.slice(0, 8) }}</cf-chip>
                  </div>
                  <div class="hitl-meta">
                    <span>trace <span class="mono">{{ task.traceId.slice(0, 8) }}</span></span>
                    <span>·</span>
                    <span>queued {{ relTime(task.createdAtUtc) }}</span>
                    @if (task.subflowPath && task.subflowPath.length > 0) {
                      <span>·</span>
                      <span class="mono">↳ {{ task.subflowPath.join('/') }}</span>
                    }
                  </div>
                  @if (task.inputPreview) {
                    <div class="hitl-preview">{{ task.inputPreview }}</div>
                  }
                </div>
                <div class="row" style="flex-direction: column; align-items: flex-end; gap: 6px">
                  <cf-chip variant="warn" dot>{{ task.state }}</cf-chip>
                </div>
              </article>
            }
          </div>

          <div class="stack">
            @if (selectedTask(); as active) {
              <cf-card title="Task detail">
                <ng-template #cardRight><cf-chip mono>#{{ active.id }}</cf-chip></ng-template>
                <div class="stack">
                  <div>
                    <div class="field-label">Agent</div>
                    <div class="mono" style="font-size: 13px; margin-top: 2px">
                      {{ active.agentKey }} <cf-chip mono style="margin-left: 4px">v{{ active.agentVersion }}</cf-chip>
                    </div>
                  </div>
                  <div>
                    <div class="field-label">Trace</div>
                    <a class="mono-link" style="font-size: 13px" [routerLink]="['/traces', active.traceId]">
                      {{ active.traceId }} ↗
                    </a>
                  </div>
                  <div>
                    <div class="field-label">Round</div>
                    <div class="mono" style="font-size: 12px">{{ active.roundId }}</div>
                  </div>
                  @if (active.subflowPath && active.subflowPath.length > 0) {
                    <div>
                      <div class="field-label">Subflow path</div>
                      <div class="mono" style="font-size: 12px">{{ active.subflowPath.join(' › ') }}</div>
                    </div>
                  }
                  @if (active.inputPreview) {
                    <div>
                      <div class="field-label">Input preview</div>
                      <pre class="payload-view" style="max-height: 200px; margin-top: 6px">{{ active.inputPreview }}</pre>
                    </div>
                  }

                  <div class="sep"></div>
                  <p class="muted small" style="margin: 0">
                    Submit a decision from the trace detail view — the agent's output template drives the form fields.
                  </p>
                  <div class="row" style="justify-content: flex-end">
                    <button type="button" cf-button variant="primary" icon="chevR"
                            (click)="openTrace(active)">Open in trace</button>
                  </div>
                </div>
              </cf-card>
            } @else {
              <cf-card>
                <div class="muted">Select a task to inspect the agent, trace, and input preview.</div>
              </cf-card>
            }
          </div>
        </div>
      }
    </div>
  `,
  styles: [`
    .hitl-card { cursor: default; }
  `]
})
export class HitlQueueComponent {
  private readonly api = inject(TracesApi);
  private readonly router = inject(Router);

  readonly tasks = signal<HitlTask[]>([]);
  readonly loading = signal(true);
  readonly selectedId = signal<number | null>(null);

  readonly pendingCount = computed(() => this.tasks().filter(t => t.state === 'Pending').length);
  readonly selectedTask = computed<HitlTask | null>(() => {
    const id = this.selectedId();
    if (id === null) return this.tasks()[0] ?? null;
    return this.tasks().find(t => t.id === id) ?? null;
  });

  constructor() { this.reload(); }

  reload(): void {
    this.loading.set(true);
    this.api.pendingHitl().subscribe({
      next: tasks => {
        this.tasks.set(tasks);
        if (this.selectedId() === null && tasks.length > 0) {
          this.selectedId.set(tasks[0].id);
        }
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  openTrace(task: HitlTask): void {
    this.router.navigate(['/traces', task.traceId]);
  }

  relTime = relativeTime;
}
