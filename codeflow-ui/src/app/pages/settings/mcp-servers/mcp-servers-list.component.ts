import { Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { McpServersApi } from '../../../core/mcp-servers.api';
import { McpServer } from '../../../core/models';
import { PageHeaderComponent } from '../../../ui/page-header.component';
import { ButtonComponent } from '../../../ui/button.component';
import { ChipComponent, ChipVariant } from '../../../ui/chip.component';

@Component({
  selector: 'cf-mcp-servers-list',
  standalone: true,
  imports: [
    DatePipe, RouterLink,
    PageHeaderComponent, ButtonComponent, ChipComponent,
  ],
  template: `
    <div class="page">
      <cf-page-header
        title="MCP servers"
        subtitle="Model Context Protocol endpoints available to all agents on this tenant.">
        <a routerLink="/settings/mcp-servers/new">
          <button type="button" cf-button variant="primary" icon="plus">Add server</button>
        </a>
      </cf-page-header>

      @if (loading()) {
        <div class="card"><div class="card-body muted">Loading servers…</div></div>
      } @else if (error()) {
        <div class="card"><div class="card-body"><cf-chip variant="err" dot>{{ error() }}</cf-chip></div></div>
      } @else if (servers().length === 0) {
        <div class="card"><div class="card-body muted">No MCP servers configured yet.</div></div>
      } @else {
        <div class="card" style="overflow: hidden">
          <table class="table">
            <thead>
              <tr>
                <th>Key</th>
                <th>Name</th>
                <th>Transport</th>
                <th>Endpoint</th>
                <th>Auth</th>
                <th>Health</th>
                <th>Last verified</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (server of servers(); track server.id) {
                <tr (click)="open(server.id)">
                  <td class="mono" style="font-weight: 500">{{ server.key }}</td>
                  <td>{{ server.displayName }}</td>
                  <td><cf-chip mono>{{ server.transport }}</cf-chip></td>
                  <td class="mono small muted" style="max-width: 280px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap">{{ server.endpointUrl }}</td>
                  <td>
                    @if (server.hasBearerToken) { <cf-chip mono>Bearer</cf-chip> }
                    @else { <cf-chip mono>none</cf-chip> }
                  </td>
                  <td>
                    <cf-chip [variant]="healthVariant(server)" dot [title]="server.lastVerificationError ?? ''">{{ server.healthStatus }}</cf-chip>
                  </td>
                  <td class="muted small">
                    @if (server.lastVerifiedAtUtc) {
                      {{ server.lastVerifiedAtUtc | date:'medium' }}
                    } @else {
                      —
                    }
                  </td>
                  <td class="actions">
                    <button type="button" cf-button size="sm"
                            (click)="$event.stopPropagation(); verify(server)"
                            [disabled]="busy() === server.id">
                      {{ busy() === server.id ? 'Verifying…' : 'Verify' }}
                    </button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        @if (unhealthy(); as bad) {
          <div class="card">
            <div class="card-header">
              <h3>{{ bad.displayName }} — last verification error</h3>
            </div>
            <div class="card-body">
              <div class="trace-failure">
                <strong>{{ bad.healthStatus }}:</strong>
                {{ bad.lastVerificationError ?? 'No details provided by the server.' }}
              </div>
            </div>
          </div>
        }
      }
    </div>
  `,
})
export class McpServersListComponent {
  private readonly api = inject(McpServersApi);
  private readonly router = inject(Router);

  readonly servers = signal<McpServer[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly busy = signal<number | null>(null);

  readonly unhealthy = computed(() => this.servers().find(s => s.healthStatus === 'Unhealthy' && s.lastVerificationError));

  constructor() { this.reload(); }

  open(id: number): void { this.router.navigate(['/settings/mcp-servers', id]); }

  healthVariant(server: McpServer): ChipVariant {
    if (server.healthStatus === 'Healthy') return 'ok';
    if (server.healthStatus === 'Unhealthy') return 'err';
    return 'default';
  }

  reload(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.list().subscribe({
      next: servers => { this.servers.set(servers); this.loading.set(false); },
      error: err => {
        this.error.set(err?.message ?? 'Failed to load MCP servers');
        this.loading.set(false);
      },
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
      error: () => this.busy.set(null),
    });
  }
}
