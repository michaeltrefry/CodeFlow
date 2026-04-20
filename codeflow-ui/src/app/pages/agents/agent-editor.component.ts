import { Component, computed, inject, input, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { AgentsApi } from '../../core/agents.api';
import { AgentRolesApi } from '../../core/agent-roles.api';
import { HostToolsApi } from '../../core/host-tools.api';
import { McpServersApi } from '../../core/mcp-servers.api';
import {
  AgentConfig,
  AgentRole,
  AgentRoleGrant,
  HostTool,
  McpServer,
} from '../../core/models';
import { ToolPickerComponent, McpServerToolCatalog } from '../../shared/tool-picker/tool-picker.component';

@Component({
  selector: 'cf-agent-editor',
  standalone: true,
  imports: [FormsModule, RouterLink, ToolPickerComponent],
  template: `
    <header class="page-header">
      <div>
        <h1>{{ existingKey() ? 'New version of ' + existingKey() : 'New agent' }}</h1>
        <p class="muted">Saving always creates a new immutable version. Tool access flows through assigned roles.</p>
      </div>
      <a routerLink="/agents"><button class="secondary">Cancel</button></a>
    </header>

    <form (submit)="submit($event)">
      @if (!existingKey()) {
        <div class="form-field">
          <label>Agent key</label>
          <input [(ngModel)]="key" name="key" required placeholder="reviewer-v1" />
          <div class="muted small">Lowercase letters, digits, '-' or '_'.</div>
        </div>
      }

      <div class="form-field">
        <label>Type</label>
        <select [(ngModel)]="type" name="type">
          <option value="agent">Agent (LLM)</option>
          <option value="hitl">HITL (Human reviewer)</option>
        </select>
      </div>

      <div class="form-field">
        <label>Name</label>
        <input [(ngModel)]="name" name="name" placeholder="Technical reviewer" />
      </div>

      <div class="form-field">
        <label>Description</label>
        <textarea [(ngModel)]="description" name="description" rows="2"></textarea>
      </div>

      @if (type() === 'agent') {
        <div class="grid-two">
          <div class="form-field">
            <label>Provider</label>
            <select [(ngModel)]="provider" name="provider">
              <option value="openai">OpenAI</option>
              <option value="anthropic">Anthropic</option>
              <option value="lmstudio">LM Studio</option>
            </select>
          </div>
          <div class="form-field">
            <label>Model</label>
            <input [(ngModel)]="model" name="model" placeholder="gpt-5" />
          </div>
        </div>

        <div class="form-field">
          <label>System prompt</label>
          <textarea [(ngModel)]="systemPrompt" name="systemPrompt" rows="4"></textarea>
        </div>

        <div class="form-field">
          <label>Prompt template</label>
          <textarea [(ngModel)]="promptTemplate" name="promptTemplate" rows="4" placeholder="Review the following input: {{ '{{input}}' }}"></textarea>
        </div>

        <div class="grid-two">
          <div class="form-field">
            <label>Max tokens</label>
            <input type="number" [(ngModel)]="maxTokens" name="maxTokens" min="1" />
          </div>
          <div class="form-field">
            <label>Temperature</label>
            <input type="number" [(ngModel)]="temperature" name="temperature" step="0.1" min="0" max="2" />
          </div>
        </div>
      }

      @if (error()) {
        <div class="tag error">{{ error() }}</div>
      }

      <div class="row" style="margin-top: 1rem;">
        <button type="submit" [disabled]="saving()">
          {{ saving() ? 'Saving…' : 'Save new version' }}
        </button>
      </div>
    </form>

    @if (existingKey()) {
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
                <input type="checkbox" [checked]="isAssigned(role.id)" (change)="toggleRole(role, $event)" />
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
        }

        @if (assignedRoles().length > 0) {
          <div class="effective">
            <h3>Effective tools</h3>
            <p class="muted small">
              Derived from the selected roles. This is what the agent can call at runtime.
            </p>
            <cf-tool-picker
              [hostTools]="hostTools()"
              [mcpServers]="mcpCatalogs()"
              [value]="effectiveGrants()"
              [readOnly]="true" />
          </div>
        }
      </section>
    } @else {
      <p class="muted small role-hint">Roles are assigned after the agent is created.</p>
    }
  `,
  styles: [`
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 1.5rem; gap: 1rem; }
    .muted { color: var(--color-muted); }
    .small { font-size: 0.8rem; }
    .role-hint { margin-top: 2rem; }
    .roles-section { margin-top: 2rem; }
    .section-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 0.75rem; }
    .section-header h2 { margin: 0; font-size: 1.1rem; }
    .role-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); gap: 0.5rem; }
    .role-option { display: flex; align-items: center; gap: 0.5rem; padding: 0.6rem 0.75rem; cursor: pointer; }
    .role-option.selected { border-color: var(--color-accent); background: rgba(56,189,248,0.05); }
    .role-key { font-family: var(--font-mono, monospace); font-weight: 600; }
    .role-name { margin-top: 0.15rem; }
    .effective { margin-top: 1.5rem; }
    .effective h3 { margin: 0 0 0.25rem; font-size: 1rem; }
  `]
})
export class AgentEditorComponent implements OnInit {
  private readonly agentsApi = inject(AgentsApi);
  private readonly rolesApi = inject(AgentRolesApi);
  private readonly hostToolsApi = inject(HostToolsApi);
  private readonly mcpApi = inject(McpServersApi);
  private readonly router = inject(Router);

  readonly existingKey = input<string | undefined>(undefined, { alias: 'key' });

  readonly key = signal('');
  readonly type = signal<'agent' | 'hitl'>('agent');
  readonly name = signal('');
  readonly description = signal('');
  readonly provider = signal<'openai' | 'anthropic' | 'lmstudio'>('openai');
  readonly model = signal('gpt-5');
  readonly systemPrompt = signal('');
  readonly promptTemplate = signal('');
  readonly maxTokens = signal<number | undefined>(undefined);
  readonly temperature = signal<number | undefined>(undefined);

  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  readonly rolesLoading = signal(false);
  readonly allRoles = signal<AgentRole[]>([]);
  readonly assignedRoles = signal<AgentRole[]>([]);
  readonly assignmentsSaving = signal(false);

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
    const existing = this.existingKey();
    if (existing) {
      this.key.set(existing);
      this.agentsApi.getLatest(existing).subscribe({
        next: version => {
          const config = version.config ?? {};
          this.type.set(version.type === 'hitl' ? 'hitl' : 'agent');
          this.name.set((config['name'] as string) ?? '');
          this.description.set((config['description'] as string) ?? '');
          this.provider.set((config['provider'] as 'openai' | 'anthropic' | 'lmstudio') ?? 'openai');
          this.model.set((config['model'] as string) ?? 'gpt-5');
          this.systemPrompt.set((config['systemPrompt'] as string) ?? '');
          this.promptTemplate.set((config['promptTemplate'] as string) ?? '');
          this.maxTokens.set(config['maxTokens'] as number | undefined);
          this.temperature.set(config['temperature'] as number | undefined);
        }
      });

      this.loadRolesAndCatalogs(existing);
    }
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
    const existing = this.existingKey();
    if (!existing) return;
    const checked = (event.target as HTMLInputElement).checked;
    const current = this.assignedRoles();
    const nextIds = checked
      ? [...current.map(r => r.id), role.id]
      : current.map(r => r.id).filter(id => id !== role.id);

    this.assignmentsSaving.set(true);
    this.rolesApi.replaceAssignments(existing, nextIds).subscribe({
      next: next => {
        this.assignedRoles.set(next);
        this.loadGrantsForRoles(next);
        this.assignmentsSaving.set(false);
      },
      error: () => this.assignmentsSaving.set(false)
    });
  }

  submit(event: Event): void {
    event.preventDefault();
    this.saving.set(true);
    this.error.set(null);

    const config: AgentConfig = {
      type: this.type(),
      name: this.name() || undefined,
      description: this.description() || undefined,
    };

    if (this.type() === 'agent') {
      config.provider = this.provider();
      config.model = this.model();
      config.systemPrompt = this.systemPrompt() || undefined;
      config.promptTemplate = this.promptTemplate() || undefined;
      if (this.maxTokens() !== undefined) config.maxTokens = this.maxTokens();
      if (this.temperature() !== undefined) config.temperature = this.temperature();
    }

    const existingKey = this.existingKey();
    const save$ = existingKey
      ? this.agentsApi.addVersion(existingKey, config)
      : this.agentsApi.create(this.key(), config);

    save$.subscribe({
      next: result => {
        this.saving.set(false);
        this.router.navigate(['/agents', result.key]);
      },
      error: err => {
        this.saving.set(false);
        this.error.set(this.formatError(err));
      }
    });
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
