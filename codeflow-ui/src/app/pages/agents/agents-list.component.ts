import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AgentsApi } from '../../core/agents.api';
import { useAsyncList } from '../../core/async-state';
import { formatHttpError } from '../../core/format-error';
import { relativeTime } from '../../core/format-time';
import { AgentSummary } from '../../core/models';
import { PageHeaderComponent } from '../../ui/page-header.component';
import { ButtonComponent } from '../../ui/button.component';
import { ChipComponent } from '../../ui/chip.component';
import { IconComponent } from '../../ui/icon.component';
import { SegmentedComponent, SegmentedOption } from '../../ui/segmented.component';
import { ProviderIconComponent } from '../../ui/provider-icon.component';

type AgentFilter = 'all' | 'agent' | 'hitl';

@Component({
  selector: 'cf-agents-list',
  standalone: true,
  imports: [
    RouterLink,
    PageHeaderComponent, ButtonComponent, ChipComponent, IconComponent,
    SegmentedComponent, ProviderIconComponent,
  ],
  template: `
    <div class="page">
      <cf-page-header
        title="Agents"
        subtitle="Named, versioned prompt + model bundles. Each version is immutable; latest is rotated in on new runs unless pinned.">
        <button type="button" cf-button variant="ghost" icon="refresh" (click)="reload()">Refresh</button>
        <a routerLink="/agents/new">
          <button type="button" cf-button variant="primary" icon="plus">New agent</button>
        </a>
      </cf-page-header>

      <div class="list-toolbar">
        <div class="list-toolbar-left">
          <cf-segmented
            [options]="filterOptions()"
            [value]="filter()"
            (valueChange)="filter.set($any($event))">
          </cf-segmented>
        </div>
        <div class="bulk-actions">
          <span class="muted small">showing {{ visibleAgents().length }}</span>
          @if (selectedCount() > 0) {
            <button type="button" cf-button variant="ghost" size="sm" icon="trash" (click)="retireSelected()" [disabled]="retiring()">
              {{ retiring() ? 'Retiring…' : 'Retire selected' }}
            </button>
          }
        </div>
      </div>

      @if (retireError()) {
        <div class="card"><div class="card-body"><cf-chip variant="err" dot>{{ retireError() }}</cf-chip></div></div>
      }

      @if (loading()) {
        <div class="card"><div class="card-body muted">Loading agents…</div></div>
      } @else if (error()) {
        <div class="card"><div class="card-body"><cf-chip variant="err" dot>{{ error() }}</cf-chip></div></div>
      } @else if (agents().length === 0) {
        <div class="card"><div class="card-body muted">No agents yet. Create one to get started.</div></div>
      } @else {
        <div class="agent-grid">
          @for (agent of visibleAgents(); track agent.key) {
            <a class="agent-card" [routerLink]="['/agents', agent.key]">
              <div class="agent-card-head">
                <input
                  type="checkbox"
                  class="select-box"
                  [checked]="isSelected(agent.key)"
                  (click)="$event.stopPropagation()"
                  (change)="toggleSelected(agent.key, $event)" />
                <div style="min-width: 0; flex: 1">
                  <div class="agent-key">{{ agent.key }}</div>
                  <div class="agent-name">{{ agent.name ?? '—' }}</div>
                </div>
                <div class="agent-type-ico" [class.hitl]="agent.type === 'hitl'">
                  <cf-icon [name]="agent.type === 'hitl' ? 'hitl' : 'bot'"></cf-icon>
                </div>
              </div>
              <div class="agent-tags">
                <cf-chip variant="accent" mono>v{{ agent.latestVersion }}</cf-chip>
                @if (agent.type === 'hitl') {
                  <cf-chip mono>hitl</cf-chip>
                } @else {
                  @if (agent.provider) {
                    <cf-chip mono>
                      <cf-provider-icon [provider]="agent.provider"></cf-provider-icon>
                      {{ agent.provider }}
                    </cf-chip>
                  }
                  @if (agent.model) {
                    <cf-chip mono>{{ agent.model }}</cf-chip>
                  }
                }
              </div>
              <div class="agent-stamp">
                <span>updated {{ relTime(agent.latestCreatedAtUtc) }}</span>
                @if (agent.latestCreatedBy) {
                  <span>·</span>
                  <span class="mono">&#64;{{ agent.latestCreatedBy }}</span>
                }
              </div>
            </a>
          }
        </div>
      }
    </div>
  `,
})
export class AgentsListComponent {
  private readonly agentsApi = inject(AgentsApi);
  private readonly agentsList = useAsyncList(
    () => this.agentsApi.list(),
    { errorMessage: 'Failed to load agents' },
  );

  readonly agents = this.agentsList.items;
  readonly loading = this.agentsList.loading;
  readonly error = this.agentsList.error;
  readonly filter = signal<AgentFilter>('all');
  readonly selectedKeys = signal<Set<string>>(new Set());
  readonly retiring = signal(false);
  readonly retireError = signal<string | null>(null);
  readonly selectedCount = computed(() => this.selectedKeys().size);

  readonly visibleAgents = computed(() => {
    const f = this.filter();
    return f === 'all' ? this.agents() : this.agents().filter(a => a.type === f);
  });

  readonly filterOptions = computed<SegmentedOption[]>(() => {
    const all = this.agents();
    return [
      { value: 'all', label: `All (${all.length})` },
      { value: 'agent', label: `LLM (${all.filter(a => a.type === 'agent').length})` },
      { value: 'hitl', label: `HITL (${all.filter(a => a.type === 'hitl').length})` },
    ];
  });

  constructor() { this.reload(); }

  reload(): void {
    this.selectedKeys.set(new Set());
    this.agentsList.reload();
  }

  relTime = relativeTime;

  isSelected(key: string): boolean {
    return this.selectedKeys().has(key);
  }

  toggleSelected(key: string, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    const next = new Set(this.selectedKeys());
    checked ? next.add(key) : next.delete(key);
    this.selectedKeys.set(next);
  }

  retireSelected(): void {
    const keys = [...this.selectedKeys()];
    if (keys.length === 0 || this.retiring()) return;
    this.retiring.set(true);
    this.retireError.set(null);
    this.agentsApi.retireMany(keys).subscribe({
      next: () => {
        this.retiring.set(false);
        this.reload();
      },
      error: err => {
        this.retiring.set(false);
        this.retireError.set(formatHttpError(err, 'Failed to retire selected agents.'));
      }
    });
  }
}
