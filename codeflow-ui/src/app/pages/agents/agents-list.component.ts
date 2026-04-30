import { Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { AgentsApi } from '../../core/agents.api';
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
        <span class="muted small">showing {{ visibleAgents().length }}</span>
      </div>

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
  private readonly router = inject(Router);

  readonly agents = signal<AgentSummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly filter = signal<AgentFilter>('all');

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
    this.loading.set(true);
    this.agentsApi.list().subscribe({
      next: results => { this.agents.set(results); this.loading.set(false); },
      error: err => {
        this.error.set(err?.message ?? 'Failed to load agents');
        this.loading.set(false);
      },
    });
  }

  relTime = relativeTime;
}
