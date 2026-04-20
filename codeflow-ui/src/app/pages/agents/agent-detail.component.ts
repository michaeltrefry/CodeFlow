import { Component, inject, input, signal, OnInit } from '@angular/core';
import { DatePipe, JsonPipe } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { AgentsApi } from '../../core/agents.api';
import { AgentVersion, AgentVersionSummary } from '../../core/models';

@Component({
  selector: 'cf-agent-detail',
  standalone: true,
  imports: [RouterLink, DatePipe, JsonPipe],
  template: `
    <header class="page-header">
      <div>
        <h1>
          {{ key() }}
          @if (retired()) {
            <span class="tag error">Retired</span>
          }
        </h1>
        @if (viewing(); as v) {
          <p class="muted">Viewing v{{ v.version }} &middot; created {{ v.createdAtUtc | date:'medium' }} by {{ v.createdBy ?? 'unknown' }}</p>
        }
        @if (retired()) {
          <p class="muted">This agent is retired. Running workflows continue to use their pinned version, but new workflows cannot reference it.</p>
        }
      </div>
      <div class="row">
        @if (!retired()) {
          <a [routerLink]="['/agents', key()]" [queryParams]="{ mode: 'edit' }">
            <button routerLink="/agents/new" [queryParams]="{ key: key() }">New version</button>
          </a>
          <button class="secondary" (click)="retire()" [disabled]="retiring()">
            {{ retiring() ? 'Retiring…' : 'Retire agent' }}
          </button>
        }
        <a routerLink="/agents"><button class="secondary">Back</button></a>
      </div>
    </header>

    @if (retireError()) {
      <p class="tag error">{{ retireError() }}</p>
    }

    <div class="grid-two">
      <div>
        <h3>Versions</h3>
        <div class="stack">
          @for (v of versions(); track v.version) {
            <button
              class="version-btn"
              [class.active]="v.version === viewing()?.version"
              (click)="select(v.version)">
              v{{ v.version }}
              <span class="small muted">{{ v.createdAtUtc | date:'mediumDate' }}</span>
            </button>
          }
        </div>
      </div>

      <div>
        @if (viewing(); as version) {
          <h3>Configuration</h3>
          <pre class="card monospace json">{{ version.config | json }}</pre>
        }
      </div>
    </div>
  `,
  styles: [`
    .version-btn {
      background: var(--color-surface);
      border: 1px solid var(--color-border);
      color: var(--color-text);
      padding: 0.5rem 0.75rem;
      text-align: left;
      cursor: pointer;
      display: flex;
      justify-content: space-between;
      align-items: center;
      border-radius: 4px;
    }
    .version-btn.active {
      border-color: var(--color-accent);
      background: rgba(56,189,248,0.08);
    }
    .small {
      font-size: 0.8rem;
    }
    pre.json {
      white-space: pre-wrap;
      word-break: break-word;
      max-height: 480px;
      overflow: auto;
    }
  `]
})
export class AgentDetailComponent implements OnInit {
  private readonly api = inject(AgentsApi);
  private readonly router = inject(Router);
  readonly key = input.required<string>();

  readonly versions = signal<AgentVersionSummary[]>([]);
  readonly viewing = signal<AgentVersion | null>(null);
  readonly retired = signal(false);
  readonly retiring = signal(false);
  readonly retireError = signal<string | null>(null);

  ngOnInit(): void {
    const key = this.key();
    this.api.versions(key).subscribe({
      next: versions => {
        this.versions.set(versions);
        if (versions.length) {
          this.select(versions[0].version);
        }
      }
    });
  }

  select(version: number): void {
    this.api.getVersion(this.key(), version).subscribe({
      next: v => {
        this.viewing.set(v);
        this.retired.set(v.isRetired);
      }
    });
  }

  retire(): void {
    const key = this.key();
    if (!confirm(`Retire agent "${key}"? Running workflows keep their pinned version, but new workflows cannot use it. This cannot be undone.`)) {
      return;
    }
    this.retiring.set(true);
    this.retireError.set(null);
    this.api.retire(key).subscribe({
      next: () => {
        this.retiring.set(false);
        this.router.navigate(['/agents']);
      },
      error: err => {
        this.retiring.set(false);
        this.retireError.set(err?.error?.error ?? err?.message ?? 'Retire failed');
      }
    });
  }
}
