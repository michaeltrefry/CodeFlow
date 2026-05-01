import { Component, DestroyRef, computed, inject, input, signal, OnInit } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
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
  AgentVersion,
  AgentVersionSummary,
  HostTool,
  LLM_PROVIDER_DISPLAY_NAMES,
  LlmProviderKey,
  McpServer,
} from '../../core/models';
import { ToolPickerComponent, McpServerToolCatalog } from '../../shared/tool-picker/tool-picker.component';
import { PageHeaderComponent } from '../../ui/page-header.component';
import { ButtonComponent } from '../../ui/button.component';
import { ChipComponent } from '../../ui/chip.component';
import { CardComponent } from '../../ui/card.component';
import { TabsComponent, TabItem } from '../../ui/tabs.component';

type DetailTab = 'identity' | 'prompt' | 'model' | 'outputs' | 'roles' | 'json';

interface ReadOnlyOutputRow {
  kind: string;
  description: string | null;
  payloadExample: unknown;
  template: string;
}

interface ReadOnlyFallbackRow {
  port: string;
  template: string;
}

@Component({
  selector: 'cf-agent-detail',
  standalone: true,
  imports: [
    CommonModule, RouterLink, DatePipe, ToolPickerComponent,
    PageHeaderComponent, ButtonComponent, ChipComponent, CardComponent, TabsComponent,
  ],
  template: `
    <div class="page">
    <cf-page-header [title]="key()">
      @if (!retired()) {
        <a [routerLink]="['/agents', key(), 'test']">
          <button type="button" cf-button variant="ghost">Test agent</button>
        </a>
        <a [routerLink]="['/agents', key(), 'edit']">
          <button type="button" cf-button variant="primary" icon="plus">New version</button>
        </a>
        <button type="button" cf-button variant="danger" (click)="retire()" [disabled]="retiring()">
          {{ retiring() ? 'Retiring…' : 'Retire agent' }}
        </button>
      }
      <a routerLink="/agents"><button type="button" cf-button variant="ghost" icon="back">Back</button></a>
      <div page-header-body>
        <div class="trace-header-meta">
          @if (retired()) { <cf-chip variant="err" dot>Retired</cf-chip> }
          <cf-chip [variant]="type() === 'hitl' ? 'accent' : 'default'" mono>{{ type() === 'hitl' ? 'HITL' : 'LLM agent' }}</cf-chip>
          @if (viewing(); as v) {
            <cf-chip mono>v{{ v.version }}</cf-chip>
            <cf-chip>created {{ v.createdAtUtc | date:'medium' }}</cf-chip>
            <cf-chip mono>&#64;{{ v.createdBy ?? 'unknown' }}</cf-chip>
          }
        </div>
        @if (retired()) {
          <p class="muted small" style="margin-top: 8px">This agent is retired. Running workflows continue to use their pinned version, but new workflows cannot reference it.</p>
        }
      </div>
    </cf-page-header>

    @if (retireError()) {
      <div class="trace-failure"><strong>Retire failed:</strong> {{ retireError() }}</div>
    }

    @if (versions().length > 1) {
      <div class="version-bar">
        <label class="field">
          <span class="field-label">Version</span>
          <select
            class="select mono version-select"
            [value]="viewing()?.version ?? ''"
            (change)="selectFromEvent($event)">
            @for (v of versions(); track v.version) {
              <option [value]="v.version">
                v{{ v.version }} · {{ v.createdAtUtc | date:'mediumDate' }}
              </option>
            }
          </select>
        </label>
      </div>
    }

    <div class="card" style="padding: 0 20px">
      <cf-tabs [items]="tabs()" [value]="tab()" (valueChange)="tab.set($any($event))"></cf-tabs>
    </div>

    @if (tab() === 'identity') {
      <cf-card>
        <div class="form-section">
          <div class="form-section-head">
            <h3>Identity</h3>
            <p>Read-only view of how this version is configured.</p>
          </div>
          <div class="form-grid">
            <label class="field">
              <span class="field-label">Agent key</span>
              <input class="input mono" [value]="key()" readonly />
            </label>
            <label class="field">
              <span class="field-label">Display name</span>
              <input class="input" [value]="displayName()" readonly placeholder="(no display name)" />
            </label>
            <label class="field">
              <span class="field-label">Type</span>
              <div class="seg ro" style="width: fit-content">
                <button type="button" disabled [attr.data-active]="type() === 'agent' ? 'true' : null">LLM agent</button>
                <button type="button" disabled [attr.data-active]="type() === 'hitl' ? 'true' : null">HITL</button>
              </div>
            </label>
            <label class="field span-2">
              <span class="field-label">Description</span>
              <textarea class="textarea" rows="2" readonly
                        [value]="description()"
                        placeholder="(no description)"
                        style="font-family: var(--font-sans); font-size: var(--fs-md)"></textarea>
            </label>
          </div>
        </div>
      </cf-card>
    }

    @if (tab() === 'prompt') {
      @if (type() === 'agent') {
        <cf-card>
          <div class="form-section">
            <div class="form-section-head">
              <h3>System prompt</h3>
              <p>Prepended to every call.</p>
            </div>
            <div class="code-field">
              <div class="code-field-head"><span>system.md</span><span>markdown</span></div>
              <textarea class="textarea" rows="8" readonly
                        [value]="systemPrompt()"
                        placeholder="(no system prompt)"
                        style="border: 0; border-radius: 0; background: var(--bg)"></textarea>
            </div>
          </div>
          <div class="form-section">
            <div class="form-section-head">
              <h3>Prompt template</h3>
              <p>Rendered per-round with Scriban substitution.</p>
            </div>
            <div class="code-field">
              <div class="code-field-head"><span>input.scriban</span><span>scriban</span></div>
              <textarea class="textarea mono" rows="18" readonly
                        [value]="promptTemplate()"
                        placeholder="(no prompt template)"
                        style="border: 0; border-radius: 0; background: var(--bg); min-height: 28rem; resize: vertical"></textarea>
            </div>
          </div>
        </cf-card>
      } @else {
        <cf-card>
          <div class="form-section">
            <div class="form-section-head">
              <h3>Output template</h3>
              <p>Defines how reviewers enter their response.</p>
            </div>
            <div class="code-field">
              <div class="code-field-head"><span>hitl.template</span><span>form</span></div>
              <textarea class="textarea mono" rows="10" readonly
                        [value]="outputTemplate()"
                        placeholder="(no output template)"
                        style="border: 0; border-radius: 0; background: var(--bg)"></textarea>
            </div>
          </div>
        </cf-card>
      }
    }

    @if (tab() === 'model') {
      @if (type() === 'agent') {
        <cf-card>
          <div class="form-section">
            <div class="form-section-head">
              <h3>Model</h3>
              <p>Provider, model id and generation parameters.</p>
            </div>
            <div class="form-grid">
              <label class="field">
                <span class="field-label">Provider</span>
                <input class="input mono" [value]="providerLabel()" readonly />
              </label>
              <label class="field">
                <span class="field-label">Model</span>
                <input class="input mono" [value]="model()" readonly />
              </label>
              <label class="field">
                <span class="field-label">Temperature</span>
                <input class="input mono" [value]="temperature() ?? ''" readonly placeholder="(default)" />
              </label>
              <label class="field">
                <span class="field-label">Max output tokens</span>
                <input class="input mono" [value]="maxTokens() ?? ''" readonly placeholder="(default)" />
              </label>
            </div>
          </div>
        </cf-card>
      } @else {
        <cf-card>
          <div class="card-body muted">
            HITL agents don't use a model.
          </div>
        </cf-card>
      }
    }

    @if (tab() === 'outputs') {
      <cf-card>
        <div class="form-section">
          <div class="form-section-head">
            <h3>Decisions</h3>
            <p>Each decision becomes an output port on workflow nodes.</p>
          </div>

          @if (outputs().length === 0) {
            <p class="muted small">No decisions declared on this version.</p>
          } @else {
            <div class="stack">
              @for (output of outputs(); track $index; let i = $index) {
                <div class="output-card">
                  <div class="row" style="justify-content: space-between">
                    <cf-chip mono>{{ output.kind || '(unnamed)' }}</cf-chip>
                  </div>
                  <div class="form-grid">
                    <label class="field">
                      <span class="field-label">Kind</span>
                      <input class="input mono" type="text" [value]="output.kind" readonly />
                    </label>
                    <label class="field">
                      <span class="field-label">Description</span>
                      <input class="input" type="text" [value]="output.description ?? ''" readonly placeholder="(none)" />
                    </label>
                    <div class="field span-2 output-advanced">
                      <button type="button" class="advanced-toggle"
                              [attr.aria-expanded]="isExpanded(i)"
                              (click)="toggleExpanded(i)">
                        <span class="advanced-caret" [attr.data-expanded]="isExpanded(i) ? 'true' : null">▸</span>
                        <span>{{ isExpanded(i) ? 'Hide' : 'Show' }} decision template &amp; payload example</span>
                        @if (!isExpanded(i) && output.template) {
                          <cf-chip mono>template</cf-chip>
                        }
                        @if (!isExpanded(i) && output.payloadExample !== null && output.payloadExample !== undefined) {
                          <cf-chip mono>payload</cf-chip>
                        }
                      </button>
                      @if (isExpanded(i)) {
                        <div class="advanced-body">
                          <label class="field">
                            <span class="field-label">Decision template <span class="muted small">(Scriban)</span></span>
                            <textarea class="textarea mono" rows="10" readonly
                                      [value]="output.template"
                                      placeholder="(no template — artifact passes through unchanged)"></textarea>
                          </label>
                          <label class="field">
                            <span class="field-label">Payload example (JSON)</span>
                            <textarea class="textarea mono" rows="8" readonly
                                      [value]="payloadExampleText(output)"
                                      placeholder="(no example)"></textarea>
                          </label>
                        </div>
                      }
                    </div>
                  </div>
                </div>
              }
            </div>
          }
        </div>

        @if (fallbackTemplates().length > 0) {
          <div class="form-section">
            <div class="form-section-head">
              <h3>Fallback templates</h3>
              <p>Catch-all templates keyed by <code>*</code> or by a port name not declared above.</p>
            </div>
            <div class="stack">
              @for (row of fallbackTemplates(); track $index) {
                <div class="output-card">
                  <div class="row" style="justify-content: space-between">
                    <cf-chip mono>{{ row.port || '(unnamed port)' }}</cf-chip>
                  </div>
                  <label class="field">
                    <span class="field-label">Output port</span>
                    <input class="input mono" type="text" [value]="row.port" readonly />
                  </label>
                  <label class="field">
                    <span class="field-label">Template</span>
                    <textarea class="textarea mono" rows="5" readonly [value]="row.template"></textarea>
                  </label>
                </div>
              }
            </div>
          </div>
        }
      </cf-card>
    }

    @if (tab() === 'roles') {
      <cf-card>
        <div class="form-section">
          <div class="form-section-head">
            <h3>Roles</h3>
            <p>Tool access = union of grants from all assigned roles.</p>
          </div>

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
              <cf-chip variant="err" dot>{{ assignError() }}</cf-chip>
            }
          }
        </div>

        <div class="form-section">
          <div class="form-section-head">
            <h3>Effective tools</h3>
            <p>Union of tool grants from currently assigned roles.</p>
          </div>
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
        </div>
      </cf-card>
    }

    @if (tab() === 'json') {
      <cf-card>
        <div class="form-section">
          <div class="form-section-head row" style="justify-content: space-between; align-items: flex-start; gap: 12px">
            <div>
              <h3>Configuration JSON</h3>
              <p>Raw config for the selected version.</p>
            </div>
            <button type="button" cf-button size="sm" variant="ghost" icon="copy"
                    (click)="copyJson()" [disabled]="!configJson()">
              {{ jsonCopied() ? 'Copied!' : 'Copy JSON' }}
            </button>
          </div>
          @if (configJson()) {
            <pre class="json-block mono">{{ configJson() }}</pre>
          } @else {
            <p class="muted small">No configuration available for this version.</p>
          }
        </div>
      </cf-card>
    }
    </div>
  `,
  styles: [`
    .small { font-size: 0.8rem; }
    .muted { color: var(--muted); }
    .version-bar {
      display: flex;
      align-items: center;
      margin-bottom: 12px;
    }
    .version-bar .field { flex-direction: row; align-items: center; gap: 8px; }
    .version-bar .field-label { margin: 0; }
    .version-select { min-width: 260px; }
    .seg.ro button { cursor: default; }
    .seg.ro button:disabled { color: var(--muted); }
    .seg.ro button[data-active="true"]:disabled { color: var(--text); }
    .json-block {
      white-space: pre-wrap;
      word-break: break-word;
      max-height: 70vh;
      overflow: auto;
      padding: 12px 14px;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--bg);
      font-family: var(--font-mono);
      font-size: var(--fs-sm);
      margin: 0;
    }
    .role-list { display: flex; flex-direction: column; gap: 0.35rem; }
    .role-option {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      padding: 0.5rem 0.75rem;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      cursor: pointer;
      background: var(--surface);
    }
    .role-option.selected {
      border-color: var(--accent);
      background: rgba(56,189,248,0.05);
    }
    .role-key { font-family: var(--font-mono, monospace); font-weight: 600; }
    .role-name { margin-top: 0.15rem; }
    .output-card {
      padding: 14px;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--surface-2);
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    .output-advanced {
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .advanced-toggle {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      padding: 6px 0;
      background: transparent;
      border: 0;
      color: var(--muted);
      font: inherit;
      cursor: pointer;
      align-self: flex-start;
    }
    .advanced-toggle:hover { color: var(--fg); }
    .advanced-caret {
      display: inline-block;
      transition: transform 0.12s ease-out;
      font-size: 0.9em;
    }
    .advanced-caret[data-expanded="true"] { transform: rotate(90deg); }
    .advanced-body {
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
  `]
})
export class AgentDetailComponent implements OnInit {
  private readonly api = inject(AgentsApi);
  private readonly rolesApi = inject(AgentRolesApi);
  private readonly hostToolsApi = inject(HostToolsApi);
  private readonly mcpApi = inject(McpServersApi);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly key = input.required<string>();

  protected readonly versions = signal<AgentVersionSummary[]>([]);
  protected readonly viewing = signal<AgentVersion | null>(null);
  protected readonly retired = signal(false);
  protected readonly retiring = signal(false);
  protected readonly retireError = signal<string | null>(null);

  protected readonly rolesLoading = signal(false);
  protected readonly allRoles = signal<AgentRole[]>([]);
  protected readonly assignedRoles = signal<AgentRole[]>([]);
  protected readonly assignmentsSaving = signal(false);
  protected readonly assignError = signal<string | null>(null);

  protected readonly hostTools = signal<HostTool[]>([]);
  protected readonly mcpCatalogs = signal<McpServerToolCatalog[]>([]);
  protected readonly grantsByRole = signal<Record<number, AgentRoleGrant[]>>({});

  protected readonly tab = signal<DetailTab>('identity');
  protected readonly expandedOutputs = signal<Set<number>>(new Set<number>());
  protected readonly jsonCopied = signal(false);

  protected readonly config = computed<AgentConfig | null>(() => this.viewing()?.config ?? null);

  protected readonly type = computed<'agent' | 'hitl'>(() => {
    const fromVersion = this.viewing()?.type;
    if (fromVersion === 'hitl') return 'hitl';
    const fromConfig = this.config()?.['type'];
    return fromConfig === 'hitl' ? 'hitl' : 'agent';
  });

  protected readonly displayName = computed<string>(() => (this.config()?.['name'] as string) ?? '');
  protected readonly description = computed<string>(() => (this.config()?.['description'] as string) ?? '');
  protected readonly provider = computed<LlmProviderKey | ''>(() => this.config()?.provider ?? '');
  protected readonly providerLabel = computed<string>(() => {
    const provider = this.provider();
    return provider ? LLM_PROVIDER_DISPLAY_NAMES[provider] : '';
  });
  protected readonly model = computed<string>(() => (this.config()?.['model'] as string) ?? '');
  protected readonly temperature = computed<number | undefined>(() => this.config()?.['temperature'] as number | undefined);
  protected readonly maxTokens = computed<number | undefined>(() => this.config()?.['maxTokens'] as number | undefined);
  protected readonly systemPrompt = computed<string>(() => (this.config()?.['systemPrompt'] as string) ?? '');
  protected readonly promptTemplate = computed<string>(() => (this.config()?.['promptTemplate'] as string) ?? '');
  protected readonly outputTemplate = computed<string>(() => (this.config()?.['outputTemplate'] as string) ?? '');

  protected readonly outputs = computed<ReadOnlyOutputRow[]>(() => {
    const cfg = this.config();
    if (!cfg) return [];
    const templates = (cfg['decisionOutputTemplates'] as Record<string, string> | undefined) ?? {};
    const declared = cfg['outputs'];
    if (Array.isArray(declared) && declared.length > 0) {
      return declared.map(d => ({
        kind: d.kind ?? '',
        description: d.description ?? null,
        payloadExample: d.payloadExample ?? null,
        template: String(templates[d.kind ?? ''] ?? ''),
      }));
    }
    return [];
  });

  protected readonly fallbackTemplates = computed<ReadOnlyFallbackRow[]>(() => {
    const cfg = this.config();
    if (!cfg) return [];
    const templates = (cfg['decisionOutputTemplates'] as Record<string, string> | undefined) ?? {};
    const declared = cfg['outputs'];
    const declaredKinds = new Set<string>(
      Array.isArray(declared) ? declared.map(d => d.kind ?? '') : []
    );
    return Object.entries(templates)
      .filter(([port]) => !declaredKinds.has(port))
      .map(([port, template]) => ({ port, template: String(template ?? '') }));
  });

  protected readonly configJson = computed<string>(() => {
    const cfg = this.config();
    if (!cfg) return '';
    try { return JSON.stringify(cfg, null, 2); } catch { return ''; }
  });

  protected readonly tabs = computed<TabItem[]>(() => {
    const items: TabItem[] = [
      { value: 'identity', label: 'Identity' },
      { value: 'prompt', label: this.type() === 'hitl' ? 'Output template' : 'Prompt & output' },
    ];
    if (this.type() === 'agent') {
      items.push({ value: 'model', label: 'Model' });
    }
    items.push({ value: 'outputs', label: 'Decisions', count: this.outputs().length });
    items.push({ value: 'roles', label: 'Roles & tools' });
    items.push({ value: 'json', label: 'JSON' });
    return items;
  });

  protected readonly effectiveGrants = computed<AgentRoleGrant[]>(() => {
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

  protected readonly grantedHostTools = computed<HostTool[]>(() => {
    const granted = new Set(
      this.effectiveGrants()
        .filter(g => g.category === 'Host')
        .map(g => g.toolIdentifier.toLowerCase())
    );
    return this.hostTools().filter(t => granted.has(t.name.toLowerCase()));
  });

  protected readonly grantedMcpCatalogs = computed<McpServerToolCatalog[]>(() => {
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
    this.api.versions(key).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
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
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: ({ allRoles, assignedRoles, hostTools, mcpServers }) => {
        this.allRoles.set(allRoles.filter(r => !r.isArchived && !r.isRetired));
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
    forkJoin(servers.map(s => this.mcpApi.getTools(s.id)))
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(toolLists => {
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
    forkJoin(roles.map(r => this.rolesApi.getGrants(r.id)))
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(lists => {
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
    this.rolesApi.replaceAssignments(this.key(), nextIds).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
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
    this.api.getVersion(this.key(), version).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: v => {
        this.viewing.set(v);
        this.retired.set(v.isRetired);
        this.expandedOutputs.set(new Set<number>());
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

  isExpanded(index: number): boolean {
    return this.expandedOutputs().has(index);
  }

  toggleExpanded(index: number): void {
    const next = new Set(this.expandedOutputs());
    if (next.has(index)) {
      next.delete(index);
    } else {
      next.add(index);
    }
    this.expandedOutputs.set(next);
  }

  payloadExampleText(output: ReadOnlyOutputRow): string {
    if (output.payloadExample === null || output.payloadExample === undefined) return '';
    if (typeof output.payloadExample === 'string') return output.payloadExample;
    try { return JSON.stringify(output.payloadExample, null, 2); } catch { return String(output.payloadExample); }
  }

  copyJson(): void {
    const text = this.configJson();
    if (!text) return;
    navigator.clipboard?.writeText(text).then(
      () => {
        this.jsonCopied.set(true);
        setTimeout(() => this.jsonCopied.set(false), 1500);
      },
      () => undefined
    );
  }

  retire(): void {
    const key = this.key();
    if (!confirm(`Retire agent "${key}"? Running workflows keep their pinned version, but new workflows cannot use it. This cannot be undone.`)) {
      return;
    }
    this.retiring.set(true);
    this.retireError.set(null);
    this.api.retire(key).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
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
