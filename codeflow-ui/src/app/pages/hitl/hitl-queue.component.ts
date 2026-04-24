import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TracesApi } from '../../core/traces.api';
import { HitlTask } from '../../core/models';
import { PageHeaderComponent } from '../../ui/page-header.component';
import { ButtonComponent } from '../../ui/button.component';
import { ChipComponent } from '../../ui/chip.component';
import { CardComponent } from '../../ui/card.component';
import { IconComponent } from '../../ui/icon.component';

function relTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return '—';
  const diff = (Date.now() - t) / 1000;
  if (diff < 60) return `${Math.max(0, Math.round(diff))}s ago`;
  if (diff < 3600) return `${Math.round(diff / 60)}m ago`;
  if (diff < 86400) return `${Math.round(diff / 3600)}h ago`;
  return `${Math.round(diff / 86400)}d ago`;
}

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
        <div class="hitl-grid">
          @for (task of tasks(); track task.id) {
            <a class="hitl-card" [routerLink]="['/traces', task.traceId]">
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
            </a>
          }
        </div>
      }
    </div>
  `,
  styles: [`
    a.hitl-card { text-decoration: none; color: inherit; }
  `]
})
export class HitlQueueComponent {
  private readonly api = inject(TracesApi);

  readonly tasks = signal<HitlTask[]>([]);
  readonly loading = signal(true);
  readonly pendingCount = computed(() => this.tasks().filter(t => t.state === 'Pending').length);

  constructor() { this.reload(); }

  reload(): void {
    this.loading.set(true);
    this.api.pendingHitl().subscribe({
      next: tasks => { this.tasks.set(tasks); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  relTime = relTime;
}
