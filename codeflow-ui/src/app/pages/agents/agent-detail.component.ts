import { Component, computed, inject, input, signal, OnInit } from '@angular/core';
import { DatePipe, JsonPipe } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { AgentsApi } from '../../core/agents.api';
import { AgentRolesApi } from '../../core/agent-roles.api';
import { HostToolsApi } from '../../core/host-tools.api';
import { McpServersApi } from '../../core/mcp-servers.api';
import {
  AgentRole,
  AgentRoleGrant,
  AgentVersion,
  AgentVersionSummary,
  HostTool,
  McpServer,
} from '../../core/models';
import { ToolPickerComponent, McpServerToolCatalog } from '../../shared/tool-picker/tool-picker.component';

@Component({
  selector: 'cf-agent-detail',
  standalone: true,
  imports: [RouterLink, DatePipe, JsonPipe, ToolPickerComponent],
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

    <section class="roles-section">
      <header class="section-header">
        <h2>Roles</h2>
        <span class="muted small">Tool access = union of assigned roles' grants</span>
      </header>

      @if (rolesLoading()) {
        <p>Loading roles&hellip;</p>
      } @else if (allRoles().length === 0) {
        <p class="muted">
          No roles defined yet.
          <a routerLink="/settings/roles/new">Create one</a> and come back to assign it.
        </p>
      } @else {
        <div class="role-grid">
          @for (role of allRoles(); track role.id) {
            <label class="role-option card" [class.selected]="isAssigned(role.id)">
              <input type="checkbox" [checked]="isAssigned(role.id)" (change)="toggleRole(role, $event)" [disabled]="assignmentsSaving()" />
              <div>
                <div class="role-key">{{ role.key }}</div>
                <div class="role-name muted small">{{ role.displayName }}</div>
              </div>
            </label>
          }
        </div>

        @if (assignmentsSaving()) {
          <p class="muted small">Saving assignments…</p>
        }
        @if (assignError()) {
          <p class="tag error">{{ assignError() }}</p>
        }
      }

      @if (assignedRoles().length > 0) {
        <div class="effective">
          <h3>Effective tools</h3>
          <p class="muted small">Derived from the selected roles. This is what the agent can call at runtime.</p>
          <cf-tool-picker
            [hostTools]="hostTools()"
            [mcpServers]="mcpCatalogs()"
            [value]="effectiveGrants()"
            [readOnly]="true" />
        </div>
      }
    </section>

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
    .small { font-size: 0.8rem; }
    pre.json {
      white-space: pre-wrap;
      word-break: break-word;
      max-height: 480px;
      overflow: auto;
    }
    .roles-section { margin: 1.5rem 0; }
    .section-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 0.75rem; }
    .section-header h2 { margin: 0; font-size: 1.1rem; }
    .role-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); gap: 0.5rem; }
    .role-option { display: flex; align-items: center; gap: 0.5rem; padding: 0.6rem 0.75rem; cursor: pointer; }
    .role-option.selected { border-color: var(--color-accent); background: rgba(56,189,248,0.05); }
    .role-key { font-family: var(--font-mono, monospace); font-weight: 600; }
    .role-name { margin-top: 0.15rem; }
    .effective { margin-top: 1.5rem; }
    .effective h3 { margin: 0 0 0.25rem; font-size: 1rem; }
    .muted { color: var(--color-muted); }
  `]
})
export class AgentDetailComponent implements OnInit {
  private readonly api = inject(AgentsApi);
  private readonly rolesApi = inject(AgentRolesApi);
  private readonly hostToolsApi = inject(HostToolsApi);
  private readonly mcpApi = inject(McpServersApi);
  private readonly router = inject(Router);
  readonly key = input.required<string>();

  readonly versions = signal<AgentVersionSummary[]>([]);
  readonly viewing = signal<AgentVersion | null>(null);
  readonly retired = signal(false);
  readonly retiring = signal(false);
  readonly retireError = signal<string | null>(null);

  readonly rolesLoading = signal(false);
  readonly allRoles = signal<AgentRole[]>([]);
  readonly assignedRoles = signal<AgentRole[]>([]);
  readonly assignmentsSaving = signal(false);
  readonly assignError = signal<string | null>(null);

  readonly hostTools = signal<HostTool[]>([]);
  readonly mcpCatalogs = signal<McpServerToolCatalog[]>([]);
  readonly grantsByRole = signal<Record<number, AgentRoleGrant[]>>({});

  readonly effectiveGrants = computed<AgentRoleGrant[]>(() => {
    const grants = this.grantsByRole();
    const seen = new Set<string>();
    const out: AgentRoleGrant[] = [];
    for (const role of this.assignedRoles()) {
      for (const grant of grants[role.id] ?? []) {
        const key = `${grant.category}::${grant.toolIdentifier}`;
        if (!seen.has(key)) {
          seen.add(key);
          out.push(grant);
        }
      }
    }
    return out;
  });

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

    this.loadRolesAndCatalogs(key);
  }

  private loadRolesAndCatalogs(agentKey: string): void {
    this.rolesLoading.set(true);
    forkJoin({
      allRoles: this.rolesApi.list(),
      assignedRoles: this.rolesApi.getRolesForAgent(agentKey),
      hostTools: this.hostToolsApi.list(),
      mcpServers: this.mcpApi.list(),
    }).subscribe({
      next: ({ allRoles, assignedRoles, hostTools, mcpServers }) => {
        this.allRoles.set(allRoles.filter(r => !r.isArchived));
        this.assignedRoles.set(assignedRoles);
        this.hostTools.set(hostTools);
        this.loadGrantsForRoles(assignedRoles);
        this.loadMcpCatalogs(mcpServers.filter(s => !s.isArchived));
        this.rolesLoading.set(false);
      },
      error: () => this.rolesLoading.set(false)
    });
  }

  private loadMcpCatalogs(servers: McpServer[]): void {
    if (servers.length === 0) {
      this.mcpCatalogs.set([]);
      return;
    }
    forkJoin(servers.map(s => this.mcpApi.getTools(s.id))).subscribe(toolLists => {
      this.mcpCatalogs.set(servers.map<McpServerToolCatalog>((server, i) => ({
        server,
        tools: toolLists[i],
      })));
    });
  }

  private loadGrantsForRoles(roles: AgentRole[]): void {
    if (roles.length === 0) {
      this.grantsByRole.set({});
      return;
    }
    forkJoin(roles.map(r => this.rolesApi.getGrants(r.id))).subscribe(lists => {
      const map: Record<number, AgentRoleGrant[]> = {};
      roles.forEach((r, i) => { map[r.id] = lists[i]; });
      this.grantsByRole.set(map);
    });
  }

  isAssigned(roleId: number): boolean {
    return this.assignedRoles().some(r => r.id === roleId);
  }

  toggleRole(role: AgentRole, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    const current = this.assignedRoles();
    const nextIds = checked
      ? [...current.map(r => r.id), role.id]
      : current.map(r => r.id).filter(id => id !== role.id);

    this.assignmentsSaving.set(true);
    this.assignError.set(null);
    this.rolesApi.replaceAssignments(this.key(), nextIds).subscribe({
      next: next => {
        this.assignedRoles.set(next);
        this.loadGrantsForRoles(next);
        this.assignmentsSaving.set(false);
      },
      error: err => {
        this.assignError.set(err?.error?.error ?? err?.message ?? 'Failed to update roles');
        this.assignmentsSaving.set(false);
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
