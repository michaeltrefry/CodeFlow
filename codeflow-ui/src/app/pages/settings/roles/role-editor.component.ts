import { Component, computed, inject, input, numberAttribute, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { AgentRolesApi } from '../../../core/agent-roles.api';
import { formatHttpError } from '../../../core/format-error';
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
import { PageHeaderComponent } from '../../../ui/page-header.component';
import { ButtonComponent } from '../../../ui/button.component';
import { CardComponent } from '../../../ui/card.component';
import { ChipComponent } from '../../../ui/chip.component';

@Component({
  selector: 'cf-role-editor',
  standalone: true,
  imports: [
    FormsModule, RouterLink, ToolPickerComponent,
    PageHeaderComponent, ButtonComponent, CardComponent, ChipComponent,
  ],
  template: `
    <div class="page">
    <cf-page-header
      [title]="id() ? 'Edit role' : 'New role'"
      subtitle="Role edits take effect on the next agent invocation — no agent version bump.">
      <a routerLink="/settings/roles">
        <button type="button" cf-button variant="ghost" icon="back">Cancel</button>
      </a>
      <button type="button" cf-button variant="primary" icon="check" (click)="submit($event)" [disabled]="saving()">
        {{ saving() ? 'Saving…' : (id() ? 'Save changes' : 'Create role') }}
      </button>
    </cf-page-header>

    <form (submit)="submit($event)">
      <cf-card title="Role details">
        <div class="form-grid">
          <label class="field">
            <span class="field-label">Key</span>
            <input class="input mono" [(ngModel)]="key" name="key" required [disabled]="!!id()" placeholder="reader" />
          </label>
          <label class="field">
            <span class="field-label">Display name</span>
            <input class="input" [(ngModel)]="displayName" name="displayName" required placeholder="Reader" />
          </label>
          <label class="field span-2">
            <span class="field-label">Description</span>
            <textarea class="textarea" [(ngModel)]="description" name="description" rows="2"
                      placeholder="What does this role grant?"
                      style="font-family: var(--font-sans); font-size: var(--fs-md)"></textarea>
          </label>
          <label class="field span-2">
            <span class="field-label">Tags</span>
            <input
              class="input mono"
              [(ngModel)]="tagsText"
              name="tags"
              placeholder="review, ops, research" />
            <span class="field-hint">Comma-separated labels for browsing and filtering.</span>
          </label>
        </div>

        @if (error()) {
          <div class="trace-failure" style="margin-top: 10px"><strong>Save failed:</strong> {{ error() }}</div>
        }
      </cf-card>
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
                    skill #{{ ghostId }} <cf-chip variant="warn" mono>archived</cf-chip>
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
    </div>
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
  readonly tagsText = signal('');
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
        this.tagsText.set(formatTags(role.tags));
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
        this.error.set(formatHttpError(err, 'Save failed'));
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
        this.error.set(formatHttpError(err, 'Save failed'));
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
        this.error.set(formatHttpError(err, 'Save failed'));
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
        tags: parseTags(this.tagsText()),
      }).subscribe({
        next: role => {
          this.role.set(role);
          this.saving.set(false);
        },
        error: err => {
          this.error.set(formatHttpError(err, 'Save failed'));
          this.saving.set(false);
        }
      });
    } else {
      this.rolesApi.create({
        key: this.key(),
        displayName: this.displayName(),
        description: this.description() || null,
        tags: parseTags(this.tagsText()),
      }).subscribe({
        next: role => {
          this.saving.set(false);
          this.router.navigate(['/settings/roles', role.id]);
        },
        error: err => {
          this.error.set(formatHttpError(err, 'Save failed'));
          this.saving.set(false);
        }
      });
    }
  }

}

function parseTags(value: string): string[] {
  const seen = new Set<string>();
  const tags: string[] = [];
  for (const raw of value.split(',')) {
    const tag = raw.trim();
    if (!tag) continue;
    const key = tag.toLocaleLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    tags.push(tag);
  }
  return tags;
}

function formatTags(tags: readonly string[] | null | undefined): string {
  return (tags ?? []).join(', ');
}
