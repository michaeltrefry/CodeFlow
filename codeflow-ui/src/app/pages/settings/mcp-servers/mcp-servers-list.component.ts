import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { McpServersApi } from '../../../core/mcp-servers.api';
import { McpServer } from '../../../core/models';

@Component({
  selector: 'cf-mcp-servers-list',
  standalone: true,
  imports: [DatePipe, RouterLink],
  template: `
    <header class="page-header">
      <h1>MCP Servers</h1>
      <a routerLink="/settings/mcp-servers/new"><button>New MCP server</button></a>
    </header>

    @if (loading()) {
      <p>Loading servers&hellip;</p>
    } @else if (error()) {
      <p class="tag error">{{ error() }}</p>
    } @else if (servers().length === 0) {
      <p class="tag">No MCP servers configured yet.</p>
    } @else {
      <table class="mcp-table">
        <thead>
          <tr>
            <th>Key</th>
            <th>Name</th>
            <th>Transport</th>
            <th>Endpoint</th>
            <th>Health</th>
            <th>Last verified</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (server of servers(); track server.id) {
            <tr>
              <td><code>{{ server.key }}</code></td>
              <td>{{ server.displayName }}</td>
              <td><span class="tag">{{ server.transport }}</span></td>
              <td class="endpoint">{{ server.endpointUrl }}</td>
              <td>
                <span class="tag" [class.success]="server.healthStatus === 'Healthy'"
                      [class.error]="server.healthStatus === 'Unhealthy'"
                      [title]="server.lastVerificationError ?? ''">
                  {{ server.healthStatus }}
                </span>
              </td>
              <td class="stamp">
                @if (server.lastVerifiedAtUtc) {
                  {{ server.lastVerifiedAtUtc | date:'medium' }}
                } @else {
                  <span class="muted">never</span>
                }
              </td>
              <td class="actions">
                <a [routerLink]="['/settings/mcp-servers', server.id]">
                  <button class="secondary small">Edit</button>
                </a>
                <button class="secondary small" (click)="verify(server)" [disabled]="busy() === server.id">
                  {{ busy() === server.id ? 'Verifying…' : 'Verify' }}
                </button>
                <button class="secondary small" (click)="refresh(server)" [disabled]="busy() === server.id">
                  Refresh
                </button>
              </td>
            </tr>
          }
        </tbody>
      </table>
    }
  `,
  styles: [`
    .page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 1.5rem; }
    .mcp-table { width: 100%; border-collapse: collapse; }
    .mcp-table th, .mcp-table td {
      text-align: left; padding: 0.5rem 0.75rem; border-bottom: 1px solid var(--color-border);
      vertical-align: middle;
    }
    .mcp-table th { color: var(--color-muted); font-size: 0.8rem; text-transform: uppercase; font-weight: 600; }
    .endpoint { font-family: var(--font-mono, monospace); font-size: 0.85rem; }
    .stamp { color: var(--color-muted); font-size: 0.85rem; }
    .actions { display: flex; gap: 0.25rem; }
    .tag.success { background: rgba(34,197,94,0.15); color: #22c55e; }
    .tag.error { background: rgba(239,68,68,0.15); color: #ef4444; }
    .small { font-size: 0.75rem; padding: 0.2rem 0.5rem; }
    .muted { color: var(--color-muted); }
  `]
})
export class McpServersListComponent {
  private readonly api = inject(McpServersApi);

  readonly servers = signal<McpServer[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly busy = signal<number | null>(null);

  constructor() {
    this.reload();
  }

  reload(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.list().subscribe({
      next: servers => {
        this.servers.set(servers);
        this.loading.set(false);
      },
      error: err => {
        this.error.set(err?.message ?? 'Failed to load MCP servers');
        this.loading.set(false);
      }
    });
  }

  verify(server: McpServer): void {
    this.busy.set(server.id);
    this.api.verify(server.id).subscribe({
      next: result => {
        this.servers.update(list => list.map(s =>
          s.id === server.id
            ? { ...s, healthStatus: result.healthStatus, lastVerifiedAtUtc: result.lastVerifiedAtUtc, lastVerificationError: result.lastVerificationError }
            : s));
        this.busy.set(null);
      },
      error: () => this.busy.set(null)
    });
  }

  refresh(server: McpServer): void {
    this.busy.set(server.id);
    this.api.refreshTools(server.id).subscribe({
      next: result => {
        this.servers.update(list => list.map(s =>
          s.id === server.id
            ? { ...s, healthStatus: result.healthStatus, lastVerifiedAtUtc: result.lastVerifiedAtUtc, lastVerificationError: result.lastVerificationError }
            : s));
        this.busy.set(null);
      },
      error: () => this.busy.set(null)
    });
  }
}
