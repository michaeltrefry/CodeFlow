import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { AgentsApi } from '../../core/agents.api';
import { AgentSummary } from '../../core/models';

@Component({
  selector: 'cf-agents-list',
  standalone: true,
  imports: [RouterLink, DatePipe],
  template: `
    <header class="page-header">
      <h1>Agents</h1>
      <a routerLink="/agents/new"><button>New agent</button></a>
    </header>

    @if (loading()) {
      <p>Loading agents&hellip;</p>
    } @else if (error()) {
      <p class="tag error">{{ error() }}</p>
    } @else if (agents().length === 0) {
      <p class="tag">No agents yet. Create one to get started.</p>
    } @else {
      <div class="agent-grid">
        @for (agent of agents(); track agent.key) {
          <a class="card agent-card" [routerLink]="['/agents', agent.key]">
            <div class="agent-header">
              <span class="agent-key">{{ agent.key }}</span>
              <span class="tag accent">v{{ agent.latestVersion }}</span>
            </div>
            <div class="agent-meta">
              <span class="tag">{{ agent.type }}</span>
              @if (agent.provider) { <span class="tag">{{ agent.provider }}</span> }
              @if (agent.model) { <span class="tag">{{ agent.model }}</span> }
            </div>
            <div class="agent-stamp">
              updated {{ agent.latestCreatedAtUtc | date:'medium' }}
              @if (agent.latestCreatedBy) {
                by {{ agent.latestCreatedBy }}
              }
            </div>
          </a>
        }
      </div>
    }
  `,
  styles: [`
    .page-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      margin-bottom: 1.5rem;
    }
    .agent-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
      gap: 1rem;
    }
    .agent-card {
      display: block;
      color: inherit;
      cursor: pointer;
      transition: border-color 150ms ease;
    }
    .agent-card:hover {
      border-color: var(--color-accent);
    }
    .agent-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      margin-bottom: 0.5rem;
    }
    .agent-key {
      font-weight: 600;
      font-size: 1.05rem;
    }
    .agent-meta {
      display: flex;
      gap: 0.4rem;
      flex-wrap: wrap;
      margin-bottom: 0.75rem;
    }
    .agent-stamp {
      color: var(--color-muted);
      font-size: 0.8rem;
    }
  `]
})
export class AgentsListComponent {
  private readonly agentsApi = inject(AgentsApi);

  readonly agents = signal<AgentSummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  constructor() {
    this.agentsApi.list().subscribe({
      next: results => {
        this.agents.set(results);
        this.loading.set(false);
      },
      error: err => {
        this.error.set(err?.message ?? 'Failed to load agents');
        this.loading.set(false);
      }
    });
  }
}
