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
          <a [routerLink]="['/agents', key(), 'test']">
            <button class="secondary">Test Agent</button>
          </a>
          <a [routerLink]="['/agents', key(), 'edit']">
            <button>New version</button>
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

    <div class="detail-grid">
      <section class="detail-column">
        <header class="section-header">
          <h2>Versions</h2>
        </header>
        <select
          class="version-select"
          [value]="viewing()?.version ?? ''"
          (change)="selectFromEvent($event)">
          @for (v of versions(); track v.version) {
            <option [value]="v.version">
              v{{ v.version }} · {{ v.createdAtUtc | date:'mediumDate' }}
            </option>
          }
        </select>

        <header class="section-header section-header-spaced">
          <h2>Configuration</h2>
        </header>
        @if (viewing(); as version) {
          <pre class="card monospace json">{{ version.config | json }}</pre>
        }
      </section>

      <section class="detail-column">
        <header class="section-header">
          <h2>Roles</h2>
          <span class="muted small">Tool access = union of grants</span>
        </header>

        @if (rolesLoading()) {
          <p>Loading roles&hellip;</p>
        } @else if (allRoles().length === 0) {
          <p class="muted">
            No roles defined yet.
            <a routerLink="/settings/roles/new">Create one</a> and come back to assign it.
          </p>
        } @else {
          <div class="role-list">
            @for (role of allRoles(); track role.id) {
              <label class="role-option" [class.selected]="isAssigned(role.id)">
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

        <header class="section-header section-header-spaced">
          <h2>Effective Tools</h2>
        </header>
        @if (assignedRoles().length === 0) {
          <p class="muted small">No roles assigned. This agent runs with no tools.</p>
        } @else if (effectiveGrants().length === 0) {
          <p class="muted small">The assigned roles grant no tools.</p>
        } @else {
          <cf-tool-picker
            [hostTools]="grantedHostTools()"
            [mcpServers]="grantedMcpCatalogs()"
            [value]="effectiveGrants()"
            [readOnly]="true" />
        }
      </section>
    </div>
  `,
  styles: [`
    .small { font-size: 0.8rem; }
    .muted { color: var(--muted); }
    pre.json {
      white-space: pre-wrap;
      word-break: break-word;
      max-height: 480px;
      overflow: auto;
    }
    .detail-grid {
      display: grid;
      grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
      gap: 2rem;
      align-items: start;
    }
    .detail-column { display: flex; flex-direction: column; }
    .section-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 0.75rem;
    }
    .section-header.section-header-spaced { margin-top: 1.5rem; }
    .section-header h2 { margin: 0; font-size: 1.1rem; }
    .version-select {
      width: 100%;
      padding: 0.5rem 0.75rem;
      border-radius: 4px;
      border: 1px solid var(--border);
      background: var(--surface);
      color: var(--text);
    }
    .role-list { display: flex; flex-direction: column; gap: 0.35rem; }
    .role-option {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      padding: 0.5rem 0.75rem;
      border: 1px solid var(--border);
      border-radius: 4px;
      cursor: pointer;
      background: var(--surface);
    }
    .role-option.selected {
      border-color: var(--accent);
      background: rgba(56,189,248,0.05);
    }
    .role-key { font-family: var(--font-mono, monospace); font-weight: 600; }
    .role-name { margin-top: 0.15rem; }
    @media (max-width: 900px) {
      .detail-grid { grid-template-columns: minmax(0, 1fr); }
    }
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

  readonly grantedHostTools = computed<HostTool[]>(() => {
    const granted = new Set(
      this.effectiveGrants()
        .filter(g => g.category === 'Host')
        .map(g => g.toolIdentifier.toLowerCase())
    );
    return this.hostTools().filter(t => granted.has(t.name.toLowerCase()));
  });

  readonly grantedMcpCatalogs = computed<McpServerToolCatalog[]>(() => {
    const grantedByServer = new Map<string, Set<string>>();
    for (const grant of this.effectiveGrants()) {
      if (grant.category !== 'Mcp') continue;
      const parts = grant.toolIdentifier.split(':', 3);
      if (parts.length !== 3 || parts[0].toLowerCase() !== 'mcp') continue;
      const serverKey = parts[1].toLowerCase();
      const toolName = parts[2].toLowerCase();
      let set = grantedByServer.get(serverKey);
      if (!set) {
        set = new Set<string>();
        grantedByServer.set(serverKey, set);
      }
      set.add(toolName);
    }

    return this.mcpCatalogs()
      .map<McpServerToolCatalog>(catalog => ({
        server: catalog.server,
        tools: catalog.tools.filter(t =>
          grantedByServer.get(catalog.server.key.toLowerCase())?.has(t.toolName.toLowerCase()) ?? false
        ),
      }))
      .filter(catalog => catalog.tools.length > 0);
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

  selectFromEvent(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    const version = Number.parseInt(value, 10);
    if (Number.isFinite(version)) {
      this.select(version);
    }
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
