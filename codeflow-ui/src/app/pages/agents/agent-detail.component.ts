import { Component, inject, input, signal, OnInit } from '@angular/core';
import { DatePipe, JsonPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { AgentsApi } from '../../core/agents.api';
import { AgentVersion, AgentVersionSummary } from '../../core/models';

@Component({
  selector: 'cf-agent-detail',
  standalone: true,
  imports: [RouterLink, DatePipe, JsonPipe],
  template: `
    <header class="page-header">
      <div>
        <h1>{{ key() }}</h1>
        @if (viewing(); as v) {
          <p class="muted">Viewing v{{ v.version }} &middot; created {{ v.createdAtUtc | date:'medium' }} by {{ v.createdBy ?? 'unknown' }}</p>
        }
      </div>
      <div class="row">
        <a [routerLink]="['/agents', key()]" [queryParams]="{ mode: 'edit' }">
          <button routerLink="/agents/new" [queryParams]="{ key: key() }">New version</button>
        </a>
        <a routerLink="/agents"><button class="secondary">Back</button></a>
      </div>
    </header>

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
  readonly key = input.required<string>();

  readonly versions = signal<AgentVersionSummary[]>([]);
  readonly viewing = signal<AgentVersion | null>(null);

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
      next: v => this.viewing.set(v)
    });
  }
}
