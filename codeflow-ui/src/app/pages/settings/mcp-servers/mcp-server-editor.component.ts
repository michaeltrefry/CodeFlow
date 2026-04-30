import { Component, computed, inject, input, numberAttribute, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { DatePipe } from '@angular/common';
import { formatHttpError } from '../../../core/format-error';
import { McpServersApi } from '../../../core/mcp-servers.api';
import {
  BearerTokenAction,
  McpServer,
  McpServerTool,
  McpTransportKind,
} from '../../../core/models';
import { PageHeaderComponent } from '../../../ui/page-header.component';
import { ButtonComponent } from '../../../ui/button.component';
import { ChipComponent, ChipVariant } from '../../../ui/chip.component';
import { CardComponent } from '../../../ui/card.component';

@Component({
  selector: 'cf-mcp-server-editor',
  standalone: true,
  imports: [
    FormsModule, RouterLink, DatePipe,
    PageHeaderComponent, ButtonComponent, ChipComponent, CardComponent,
  ],
  template: `
    <div class="page">
    <cf-page-header [title]="id() ? 'Edit MCP server' : 'New MCP server'">
      @if (id() && !server()?.isArchived) {
        <button type="button" cf-button variant="danger" icon="trash" (click)="archive()" [disabled]="saving()">Archive</button>
      }
      <a routerLink="/settings/mcp-servers">
        <button type="button" cf-button variant="ghost" icon="back">Cancel</button>
      </a>
      <button type="button" cf-button variant="primary" icon="check" (click)="submit($event)" [disabled]="saving()">
        {{ saving() ? 'Saving…' : (id() ? 'Save changes' : 'Create server') }}
      </button>
      <div page-header-body>
        @if (server(); as s) {
          <div class="trace-header-meta">
            <cf-chip [variant]="healthVariant(s)" dot [title]="s.lastVerificationError ?? ''">{{ s.healthStatus }}</cf-chip>
            @if (s.lastVerifiedAtUtc) {
              <cf-chip>last verified {{ s.lastVerifiedAtUtc | date:'medium' }}</cf-chip>
            }
            @if (s.isArchived) {
              <cf-chip variant="warn" dot>archived</cf-chip>
            }
          </div>
          @if (s.lastVerificationError) {
            <div class="trace-failure" style="margin-top: 8px">
              <strong>Last verification error:</strong> {{ s.lastVerificationError }}
            </div>
          }
        }
      </div>
    </cf-page-header>

    <form (submit)="submit($event)">
      <cf-card title="Server connection">
        <div class="form-grid">
          <label class="field">
            <span class="field-label">Key</span>
            <input class="input mono" [(ngModel)]="key" name="key" required [disabled]="!!id()" placeholder="artifacts" />
            <span class="field-hint">Used in tool identifiers (e.g. <code>mcp:&lt;key&gt;:tool_name</code>).</span>
          </label>
          <label class="field">
            <span class="field-label">Display name</span>
            <input class="input" [(ngModel)]="displayName" name="displayName" required placeholder="Artifact Store" />
          </label>
          <label class="field">
            <span class="field-label">Transport</span>
            <select class="select" [(ngModel)]="transport" name="transport">
              <option value="StreamableHttp">Streamable HTTP (current)</option>
              <option value="HttpSse">HTTP + SSE (legacy)</option>
            </select>
          </label>
          <label class="field">
            <span class="field-label">Endpoint URL</span>
            <input class="input mono" [(ngModel)]="endpointUrl" name="endpointUrl" required placeholder="https://mcp.example.com/mcp" />
          </label>

          <label class="field span-2">
            <span class="field-label">Bearer token</span>
            @if (id() && !replacingToken()) {
              <div class="row">
                @if (server()?.hasBearerToken) {
                  <cf-chip mono>••••••••</cf-chip>
                  <button type="button" cf-button size="sm" (click)="startReplace()">Replace</button>
                  <button type="button" cf-button variant="danger" size="sm" (click)="clearToken()">Clear</button>
                } @else {
                  <span class="muted">no token set</span>
                  <button type="button" cf-button size="sm" (click)="startReplace()">Set token</button>
                }
              </div>
            } @else {
              <input type="password" class="input mono"
                     [(ngModel)]="bearerTokenValue" name="bearerToken"
                     placeholder="Bearer token (leave blank for none)" />
              @if (id()) {
                <button type="button" cf-button variant="ghost" size="sm" (click)="cancelReplace()">Cancel</button>
              }
            }
          </label>
        </div>

        @if (error()) {
          <div class="trace-failure" style="margin-top: 10px"><strong>Save failed:</strong> {{ error() }}</div>
        }
      </cf-card>
    </form>

    @if (id()) {
      <cf-card title="Discovered tools">
        <ng-template #cardRight>
          <div class="row">
            <button type="button" cf-button size="sm" (click)="verify()" [disabled]="busy()">
              {{ verifyingMessage() }}
            </button>
            <button type="button" cf-button size="sm" (click)="refresh()" [disabled]="busy()">
              {{ refreshingMessage() }}
            </button>
          </div>
        </ng-template>

        @if (toolsLoading()) {
          <div class="muted">Loading tools…</div>
        } @else if (tools().length === 0) {
          <div class="muted">No tools discovered yet. Click <strong>Refresh</strong> to fetch from the server.</div>
        } @else {
          <div class="stack">
            @for (tool of tools(); track tool.toolName) {
              <div class="output-card">
                <div class="row" style="justify-content: space-between">
                  <code class="mono">mcp:{{ server()?.key }}:{{ tool.toolName }}</code>
                  @if (tool.isMutating) {
                    <cf-chip variant="warn" mono>mutating</cf-chip>
                  }
                </div>
                @if (tool.description) {
                  <div class="muted small">{{ tool.description }}</div>
                }
              </div>
            }
          </div>
        }
      </cf-card>
    }
    </div>
  `,
  styles: [`
    .output-card {
      padding: 10px 12px;
      background: var(--surface-2);
      border: 1px solid var(--border);
      border-radius: var(--radius);
      display: flex;
      flex-direction: column;
      gap: 4px;
    }
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

  healthVariant(s: McpServer): ChipVariant {
    if (s.healthStatus === 'Healthy') return 'ok';
    if (s.healthStatus === 'Unhealthy') return 'err';
    return 'default';
  }

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
        this.error.set(formatHttpError(err, 'Save failed'));
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

  archive(): void {
    const existingId = this.id();
    if (!existingId) return;
    if (!confirm('Archive this MCP server? Agents will stop receiving its tools immediately.')) return;
    this.saving.set(true);
    this.api.archive(existingId).subscribe({
      next: () => this.router.navigate(['/settings/mcp-servers']),
      error: err => {
        this.error.set(formatHttpError(err, 'Save failed'));
        this.saving.set(false);
      }
    });
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
          this.error.set(formatHttpError(err, 'Save failed'));
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
          this.error.set(formatHttpError(err, 'Save failed'));
          this.saving.set(false);
        }
      });
    }
  }

}
