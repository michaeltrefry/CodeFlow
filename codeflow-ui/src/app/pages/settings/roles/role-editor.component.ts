import { Component, computed, inject, input, numberAttribute, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { AgentRolesApi } from '../../../core/agent-roles.api';
import { HostToolsApi } from '../../../core/host-tools.api';
import { McpServersApi } from '../../../core/mcp-servers.api';
import {
  AgentRole,
  AgentRoleGrant,
  HostTool,
  McpServer,
  McpServerTool,
} from '../../../core/models';
import { ToolPickerComponent, McpServerToolCatalog } from '../../../shared/tool-picker/tool-picker.component';

@Component({
  selector: 'cf-role-editor',
  standalone: true,
  imports: [FormsModule, RouterLink, ToolPickerComponent],
  template: `
    <header class="page-header">
      <div>
        <h1>{{ id() ? 'Edit role' : 'New role' }}</h1>
        <p class="muted">Role edits take effect on the next agent invocation — no agent version bump.</p>
      </div>
      <a routerLink="/settings/roles"><button class="secondary">Cancel</button></a>
    </header>

    <form (submit)="submit($event)">
      <div class="grid-two">
        <div class="form-field">
          <label>Key</label>
          <input [(ngModel)]="key" name="key" required [disabled]="!!id()" placeholder="reader" />
        </div>
        <div class="form-field">
          <label>Display name</label>
          <input [(ngModel)]="displayName" name="displayName" required placeholder="Reader" />
        </div>
      </div>

      <div class="form-field">
        <label>Description</label>
        <textarea [(ngModel)]="description" name="description" rows="2" placeholder="What does this role grant?"></textarea>
      </div>

      @if (error()) {
        <div class="tag error">{{ error() }}</div>
      }

      <div class="row" style="margin-top: 1rem;">
        <button type="submit" [disabled]="saving()">
          {{ saving() ? 'Saving…' : (id() ? 'Save changes' : 'Create role') }}
        </button>
      </div>
    </form>

    @if (id()) {
      <section class="grants-section">
        <header class="section-header">
          <h2>Granted tools</h2>
          <span class="muted small">{{ grants().length }} selected</span>
        </header>

        @if (hasHighRiskGrant()) {
          <div class="risk-banner">
            <strong>⚠ This role grants <code>workspace.exec</code>.</strong>
            It allows arbitrary code execution on the CodeFlow host with no sandbox.
            Do not enable on instances that serve untrusted agent authors. See
            <code>SECURITY.md</code> for the full threat model.
          </div>
        }

        @if (catalogsLoading()) {
          <p>Loading catalog&hellip;</p>
        } @else {
          <cf-tool-picker
            [hostTools]="hostTools()"
            [mcpServers]="mcpCatalogs()"
            [value]="grants()"
            (valueChange)="saveGrants($event)" />
        }

        @if (grantsSaving()) {
          <p class="muted small">Saving grants…</p>
        }
      </section>
    }
  `,
  styles: [`
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 1.5rem; gap: 1rem; }
    .muted { color: var(--color-muted); }
    .grants-section { margin-top: 2rem; }
    .section-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 0.75rem; }
    .section-header h2 { margin: 0; font-size: 1.1rem; }
    .small { font-size: 0.8rem; }
    .risk-banner {
      background: rgba(217, 83, 79, 0.1);
      border: 1px solid var(--color-danger, #d9534f);
      color: var(--color-danger, #d9534f);
      padding: 0.75rem 1rem;
      border-radius: 6px;
      margin-bottom: 1rem;
      font-size: 0.9rem;
      line-height: 1.45;
    }
    .risk-banner code { background: rgba(0,0,0,0.08); padding: 0 0.25rem; border-radius: 3px; }
  `]
})
export class RoleEditorComponent implements OnInit {
  private readonly rolesApi = inject(AgentRolesApi);
  private readonly hostToolsApi = inject(HostToolsApi);
  private readonly mcpApi = inject(McpServersApi);
  private readonly router = inject(Router);

  readonly id = input<number | undefined, unknown>(undefined, {
    transform: (v: unknown) => (v === undefined || v === null || v === '' ? undefined : numberAttribute(v)),
  });

  readonly key = signal('');
  readonly displayName = signal('');
  readonly description = signal('');
  readonly role = signal<AgentRole | null>(null);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  readonly grants = signal<AgentRoleGrant[]>([]);
  readonly grantsSaving = signal(false);

  readonly hasHighRiskGrant = computed(() =>
    this.grants().some(g => g.toolIdentifier === 'workspace.exec'));

  readonly catalogsLoading = signal(false);
  readonly hostTools = signal<HostTool[]>([]);
  readonly mcpCatalogs = signal<McpServerToolCatalog[]>([]);

  ngOnInit(): void {
    const existingId = this.id();
    if (existingId) {
      this.rolesApi.get(existingId).subscribe(role => {
        this.role.set(role);
        this.key.set(role.key);
        this.displayName.set(role.displayName);
        this.description.set(role.description ?? '');
      });
      this.rolesApi.getGrants(existingId).subscribe(g => this.grants.set(g));
      this.loadCatalogs();
    }
  }

  private loadCatalogs(): void {
    this.catalogsLoading.set(true);
    this.hostToolsApi.list().subscribe(tools => this.hostTools.set(tools));
    this.mcpApi.list().subscribe(servers => {
      const active = servers.filter(s => !s.isArchived);
      if (active.length === 0) {
        this.mcpCatalogs.set([]);
        this.catalogsLoading.set(false);
        return;
      }
      const requests = active.map(s => this.mcpApi.getTools(s.id));
      forkJoin(requests).subscribe({
        next: toolLists => {
          this.mcpCatalogs.set(active.map<McpServerToolCatalog>((server, i) => ({
            server,
            tools: toolLists[i],
          })));
          this.catalogsLoading.set(false);
        },
        error: () => this.catalogsLoading.set(false)
      });
    });
  }

  saveGrants(next: AgentRoleGrant[]): void {
    const id = this.id();
    if (!id) return;
    this.grantsSaving.set(true);
    this.rolesApi.replaceGrants(id, next).subscribe({
      next: persisted => {
        this.grants.set(persisted);
        this.grantsSaving.set(false);
      },
      error: err => {
        this.error.set(this.formatError(err));
        this.grantsSaving.set(false);
      }
    });
  }

  submit(event: Event): void {
    event.preventDefault();
    this.saving.set(true);
    this.error.set(null);

    const existingId = this.id();
    if (existingId) {
      this.rolesApi.update(existingId, {
        displayName: this.displayName(),
        description: this.description() || null,
      }).subscribe({
        next: role => {
          this.role.set(role);
          this.saving.set(false);
        },
        error: err => {
          this.error.set(this.formatError(err));
          this.saving.set(false);
        }
      });
    } else {
      this.rolesApi.create({
        key: this.key(),
        displayName: this.displayName(),
        description: this.description() || null,
      }).subscribe({
        next: role => {
          this.saving.set(false);
          this.router.navigate(['/settings/roles', role.id]);
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
