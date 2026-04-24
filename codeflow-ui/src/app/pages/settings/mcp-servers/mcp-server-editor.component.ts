import { Component, inject, input, numberAttribute, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { DatePipe } from '@angular/common';
import { McpServersApi } from '../../../core/mcp-servers.api';
import {
  BearerTokenAction,
  McpServer,
  McpServerTool,
  McpTransportKind,
} from '../../../core/models';

@Component({
  selector: 'cf-mcp-server-editor',
  standalone: true,
  imports: [FormsModule, RouterLink, DatePipe],
  template: `
    <header class="page-header">
      <div>
        <h1>{{ id() ? 'Edit MCP server' : 'New MCP server' }}</h1>
        @if (server(); as s) {
          <div class="muted small">
            Health: <span class="tag" [class.success]="s.healthStatus === 'Healthy'"
                                     [class.error]="s.healthStatus === 'Unhealthy'">{{ s.healthStatus }}</span>
            @if (s.lastVerifiedAtUtc) {
              · last verified {{ s.lastVerifiedAtUtc | date:'medium' }}
            }
            @if (s.lastVerificationError) {
              · <span class="muted">{{ s.lastVerificationError }}</span>
            }
          </div>
        }
      </div>
      <a routerLink="/settings/mcp-servers"><button class="secondary">Cancel</button></a>
    </header>

    <form (submit)="submit($event)">
      <div class="grid-two">
        <div class="form-field">
          <label>Key</label>
          <input [(ngModel)]="key" name="key" required [disabled]="!!id()" placeholder="artifacts" />
          <div class="muted small">Used in tool identifiers (e.g. <code>mcp:&lt;key&gt;:tool_name</code>).</div>
        </div>
        <div class="form-field">
          <label>Display name</label>
          <input [(ngModel)]="displayName" name="displayName" required placeholder="Artifact Store" />
        </div>
      </div>

      <div class="grid-two">
        <div class="form-field">
          <label>Transport</label>
          <select [(ngModel)]="transport" name="transport">
            <option value="StreamableHttp">Streamable HTTP (current)</option>
            <option value="HttpSse">HTTP + SSE (legacy)</option>
          </select>
        </div>
        <div class="form-field">
          <label>Endpoint URL</label>
          <input [(ngModel)]="endpointUrl" name="endpointUrl" required placeholder="https://mcp.example.com/mcp" />
        </div>
      </div>

      <div class="form-field">
        <label>Bearer token</label>
        @if (id() && !replacingToken()) {
          <div class="token-row">
            @if (server()?.hasBearerToken) {
              <span class="tag">••••••••</span>
              <button type="button" class="secondary small" (click)="startReplace()">Replace</button>
              <button type="button" class="secondary small" (click)="clearToken()">Clear</button>
            } @else {
              <span class="muted">no token set</span>
              <button type="button" class="secondary small" (click)="startReplace()">Set token</button>
            }
          </div>
        } @else {
          <input
            type="password"
            [(ngModel)]="bearerTokenValue"
            name="bearerToken"
            placeholder="Bearer token (leave blank for none)" />
          @if (id()) {
            <button type="button" class="ghost small" (click)="cancelReplace()">Cancel</button>
          }
        }
      </div>

      @if (error()) {
        <div class="tag error">{{ error() }}</div>
      }

      <div class="row" style="margin-top: 1rem;">
        <button type="submit" [disabled]="saving()">
          {{ saving() ? 'Saving…' : (id() ? 'Save changes' : 'Create server') }}
        </button>
      </div>
    </form>

    @if (id()) {
      <section class="tools-section">
        <header class="section-header">
          <h2>Discovered tools</h2>
          <button type="button" class="secondary small" (click)="verify()" [disabled]="busy()">
            {{ verifyingMessage() }}
          </button>
          <button type="button" class="secondary small" (click)="refresh()" [disabled]="busy()">
            {{ refreshingMessage() }}
          </button>
        </header>

        @if (toolsLoading()) {
          <p>Loading tools&hellip;</p>
        } @else if (tools().length === 0) {
          <p class="muted">No tools discovered yet. Click <strong>Refresh</strong> to fetch from the server.</p>
        } @else {
          <ul class="tool-list">
            @for (tool of tools(); track tool.toolName) {
              <li>
                <div class="tool-name">
                  <code>mcp:{{ server()?.key }}:{{ tool.toolName }}</code>
                  @if (tool.isMutating) {
                    <span class="tag small">mutating</span>
                  }
                </div>
                @if (tool.description) {
                  <div class="muted small">{{ tool.description }}</div>
                }
              </li>
            }
          </ul>
        }
      </section>
    }
  `,
  styles: [`
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 1.5rem; gap: 1rem; }
    .token-row { display: flex; align-items: center; gap: 0.5rem; }
    .small { font-size: 0.75rem; padding: 0.2rem 0.5rem; }
    .muted { color: var(--muted); }
    .muted.small { font-size: 0.8rem; }
    .tag.success { background: rgba(34,197,94,0.15); color: #22c55e; }
    .tag.error { background: rgba(239,68,68,0.15); color: #ef4444; }
    .tools-section { margin-top: 2rem; }
    .section-header { display: flex; align-items: center; gap: 0.75rem; margin-bottom: 0.75rem; }
    .section-header h2 { margin: 0; flex: 1; font-size: 1.1rem; }
    .tool-list { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 0.5rem; }
    .tool-list li { border: 1px solid var(--border); border-radius: 4px; padding: 0.5rem 0.75rem; }
    .tool-name { display: flex; gap: 0.5rem; align-items: center; }
  `]
})
export class McpServerEditorComponent implements OnInit {
  private readonly api = inject(McpServersApi);
  private readonly router = inject(Router);

  readonly id = input<number | undefined, unknown>(undefined, {
    transform: (v: unknown) => (v === undefined || v === null || v === '' ? undefined : numberAttribute(v)),
  });

  readonly key = signal('');
  readonly displayName = signal('');
  readonly transport = signal<McpTransportKind>('StreamableHttp');
  readonly endpointUrl = signal('');
  readonly bearerTokenValue = signal('');
  readonly replacingToken = signal(false);
  readonly server = signal<McpServer | null>(null);
  readonly tools = signal<McpServerTool[]>([]);
  readonly toolsLoading = signal(false);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly busy = signal<'verify' | 'refresh' | null>(null);

  ngOnInit(): void {
    const existingId = this.id();
    if (existingId) {
      this.api.get(existingId).subscribe(server => {
        this.server.set(server);
        this.key.set(server.key);
        this.displayName.set(server.displayName);
        this.transport.set(server.transport);
        this.endpointUrl.set(server.endpointUrl);
      });
      this.loadTools(existingId);
    } else {
      this.replacingToken.set(true); // Free-text entry for create flow
    }
  }

  private loadTools(id: number): void {
    this.toolsLoading.set(true);
    this.api.getTools(id).subscribe({
      next: tools => {
        this.tools.set(tools);
        this.toolsLoading.set(false);
      },
      error: () => this.toolsLoading.set(false)
    });
  }

  startReplace(): void {
    this.replacingToken.set(true);
    this.bearerTokenValue.set('');
  }

  cancelReplace(): void {
    this.replacingToken.set(false);
    this.bearerTokenValue.set('');
  }

  clearToken(): void {
    if (!this.id()) return;
    this.saving.set(true);
    const id = this.id()!;
    this.api.update(id, {
      displayName: this.displayName(),
      transport: this.transport(),
      endpointUrl: this.endpointUrl(),
      bearerToken: { action: 'Clear' },
    }).subscribe({
      next: server => {
        this.server.set(server);
        this.saving.set(false);
      },
      error: err => {
        this.error.set(this.formatError(err));
        this.saving.set(false);
      }
    });
  }

  verify(): void {
    if (!this.id()) return;
    this.busy.set('verify');
    this.api.verify(this.id()!).subscribe({
      next: result => {
        this.server.update(current => current
          ? { ...current, healthStatus: result.healthStatus, lastVerifiedAtUtc: result.lastVerifiedAtUtc, lastVerificationError: result.lastVerificationError }
          : current);
        this.busy.set(null);
      },
      error: () => this.busy.set(null)
    });
  }

  refresh(): void {
    if (!this.id()) return;
    this.busy.set('refresh');
    this.api.refreshTools(this.id()!).subscribe({
      next: result => {
        this.tools.set(result.tools);
        this.server.update(current => current
          ? { ...current, healthStatus: result.healthStatus, lastVerifiedAtUtc: result.lastVerifiedAtUtc, lastVerificationError: result.lastVerificationError }
          : current);
        this.busy.set(null);
      },
      error: () => this.busy.set(null)
    });
  }

  verifyingMessage(): string {
    return this.busy() === 'verify' ? 'Verifying…' : 'Verify';
  }

  refreshingMessage(): string {
    return this.busy() === 'refresh' ? 'Refreshing…' : 'Refresh tools';
  }

  submit(event: Event): void {
    event.preventDefault();
    this.saving.set(true);
    this.error.set(null);

    const existingId = this.id();
    if (existingId) {
      const action: BearerTokenAction = this.replacingToken()
        ? (this.bearerTokenValue() ? 'Replace' : 'Preserve')
        : 'Preserve';

      this.api.update(existingId, {
        displayName: this.displayName(),
        transport: this.transport(),
        endpointUrl: this.endpointUrl(),
        bearerToken: { action, value: action === 'Replace' ? this.bearerTokenValue() : undefined },
      }).subscribe({
        next: server => {
          this.server.set(server);
          this.replacingToken.set(false);
          this.bearerTokenValue.set('');
          this.saving.set(false);
        },
        error: err => {
          this.error.set(this.formatError(err));
          this.saving.set(false);
        }
      });
    } else {
      this.api.create({
        key: this.key(),
        displayName: this.displayName(),
        transport: this.transport(),
        endpointUrl: this.endpointUrl(),
        bearerToken: this.bearerTokenValue() || null,
      }).subscribe({
        next: server => {
          this.saving.set(false);
          this.router.navigate(['/settings/mcp-servers', server.id]);
        },
        error: err => {
          this.error.set(this.formatError(err));
          this.saving.set(false);
        }
      });
    }
  }

  private formatError(err: unknown): string {
    if (err && typeof err === 'object') {
      const httpErr = err as { error?: unknown; message?: string };
      if (httpErr.error && typeof httpErr.error === 'object') {
        return JSON.stringify(httpErr.error);
      }
      if (httpErr.message) {
        return httpErr.message;
      }
    }
    return 'Save failed';
  }
}
