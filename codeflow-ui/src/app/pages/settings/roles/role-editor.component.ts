import { Component, computed, inject, input, numberAttribute, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { AgentRolesApi } from '../../../core/agent-roles.api';
import { HostToolsApi } from '../../../core/host-tools.api';
import { McpServersApi } from '../../../core/mcp-servers.api';
import { SkillsApi } from '../../../core/skills.api';
import {
  AgentRole,
  AgentRoleGrant,
  HostTool,
  McpServer,
  McpServerTool,
  Skill,
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

      <section class="grants-section">
        <header class="section-header">
          <h2>Granted skills</h2>
          <span class="muted small">{{ grantedSkillIds().length }} selected</span>
        </header>

        @if (skillsLoading()) {
          <p>Loading skills&hellip;</p>
        } @else if (availableSkills().length === 0 && ghostSkillIds().length === 0) {
          <p class="muted small">
            No skills defined yet. <a routerLink="/settings/skills/new">Create one</a> to attach it to this role.
          </p>
        } @else {
          <ul class="skill-list">
            @for (skill of availableSkills(); track skill.id) {
              <li class="skill-row">
                <label>
                  <input type="checkbox"
                         [checked]="isSkillGranted(skill.id)"
                         (change)="toggleSkill(skill.id, $any($event.target).checked)" />
                  <span class="skill-name">{{ skill.name }}</span>
                </label>
              </li>
            }
            @for (ghostId of ghostSkillIds(); track ghostId) {
              <li class="skill-row ghost">
                <label>
                  <input type="checkbox"
                         [checked]="true"
                         (change)="toggleSkill(ghostId, $any($event.target).checked)" />
                  <span class="skill-name">
                    skill #{{ ghostId }} <span class="tag warn">archived</span>
                  </span>
                </label>
              </li>
            }
          </ul>
        }

        @if (skillsSaving()) {
          <p class="muted small">Saving skills…</p>
        }
      </section>
    }
  `,
  styles: [`
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 1.5rem; gap: 1rem; }
    .muted { color: var(--muted); }
    .grants-section { margin-top: 2rem; }
    .section-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 0.75rem; }
    .section-header h2 { margin: 0; font-size: 1.1rem; }
    .small { font-size: 0.8rem; }
    .skill-list { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 0.25rem; }
    .skill-row label { display: flex; gap: 0.5rem; align-items: center; padding: 0.35rem 0.5rem; border-radius: 4px; cursor: pointer; }
    .skill-row label:hover { background: var(--surface-2); }
    .skill-row.ghost label { color: var(--muted); }
    .skill-row .skill-name { font-family: var(--font-mono, monospace); font-size: 0.9rem; }
  `]
})
export class RoleEditorComponent implements OnInit {
  private readonly rolesApi = inject(AgentRolesApi);
  private readonly hostToolsApi = inject(HostToolsApi);
  private readonly mcpApi = inject(McpServersApi);
  private readonly skillsApi = inject(SkillsApi);
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

  readonly catalogsLoading = signal(false);
  readonly hostTools = signal<HostTool[]>([]);
  readonly mcpCatalogs = signal<McpServerToolCatalog[]>([]);

  readonly skillsLoading = signal(false);
  readonly skillsSaving = signal(false);
  readonly availableSkills = signal<Skill[]>([]);
  readonly grantedSkillIds = signal<number[]>([]);
  readonly ghostSkillIds = computed(() => {
    const known = new Set(this.availableSkills().map(s => s.id));
    return this.grantedSkillIds().filter(id => !known.has(id));
  });

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
      this.loadSkills(existingId);
    }
  }

  private loadSkills(roleId: number): void {
    this.skillsLoading.set(true);
    forkJoin({
      skills: this.skillsApi.list(),
      grants: this.skillsApi.getRoleGrants(roleId),
    }).subscribe({
      next: ({ skills, grants }) => {
        this.availableSkills.set(skills);
        this.grantedSkillIds.set(grants.skillIds);
        this.skillsLoading.set(false);
      },
      error: err => {
        this.error.set(this.formatError(err));
        this.skillsLoading.set(false);
      }
    });
  }

  isSkillGranted(id: number): boolean {
    return this.grantedSkillIds().includes(id);
  }

  toggleSkill(id: number, next: boolean): void {
    const roleId = this.id();
    if (!roleId) return;
    const current = new Set(this.grantedSkillIds());
    if (next) {
      current.add(id);
    } else {
      current.delete(id);
    }
    const nextIds = Array.from(current);
    this.skillsSaving.set(true);
    this.skillsApi.replaceRoleGrants(roleId, nextIds).subscribe({
      next: persisted => {
        this.grantedSkillIds.set(persisted.skillIds);
        this.skillsSaving.set(false);
      },
      error: err => {
        this.error.set(this.formatError(err));
        this.skillsSaving.set(false);
      }
    });
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
