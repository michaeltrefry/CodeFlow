import { Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AgentRolesApi } from '../../../core/agent-roles.api';
import { useAsyncList } from '../../../core/async-state';
import { formatHttpError } from '../../../core/format-error';
import { AgentRole } from '../../../core/models';
import { PageHeaderComponent } from '../../../ui/page-header.component';
import { ButtonComponent } from '../../../ui/button.component';
import { ChipComponent } from '../../../ui/chip.component';

@Component({
  selector: 'cf-roles-list',
  standalone: true,
  imports: [
    DatePipe, FormsModule, RouterLink,
    PageHeaderComponent, ButtonComponent, ChipComponent,
  ],
  template: `
    <div class="page">
      <cf-page-header
        title="Roles"
        subtitle="Assignable bundles of permissions and skill grants.">
        @if (selectedCount() > 0) {
          <button type="button" cf-button variant="ghost" icon="trash" (click)="retireSelected()" [disabled]="retiring()">
            {{ retiring() ? 'Retiring…' : 'Retire selected' }}
          </button>
        }
        <a routerLink="/settings/roles/new">
          <button type="button" cf-button variant="primary" icon="plus">New role</button>
        </a>
      </cf-page-header>

      <div class="list-toolbar">
        <div class="list-toolbar-left">
          <form class="tag-filter" (submit)="addTagFilter($event)">
            <label class="field">
              <span class="field-label">Tags</span>
              <input
                class="input mono tag-filter-input"
                name="roleTagFilter"
                list="role-tag-options"
                [ngModel]="tagFilterInput()"
                (ngModelChange)="tagFilterInput.set($event)"
                placeholder="ops, review" />
            </label>
            <button type="submit" cf-button variant="ghost" size="sm" icon="plus">Add tag</button>
            <datalist id="role-tag-options">
              @for (tag of availableTags(); track tag) {
                <option [value]="tag"></option>
              }
            </datalist>
          </form>
        </div>
        <div class="bulk-actions">
          <span class="muted small">showing {{ visibleRoles().length }}</span>
        </div>
      </div>

      @if (selectedTags().length > 0 || visibleTagOptions().length > 0) {
        <div class="tag-filter-row" aria-label="Role tag filters">
          @for (tag of selectedTags(); track tag) {
            <button type="button" class="tag-filter-chip active" (click)="removeTagFilter(tag)">
              {{ tag }} <span aria-hidden="true">×</span>
            </button>
          }
          @for (tag of visibleTagOptions(); track tag) {
            <button type="button" class="tag-filter-chip" (click)="addTag(tag)">
              {{ tag }}
            </button>
          }
          @if (selectedTags().length > 0) {
            <button type="button" cf-button variant="ghost" size="sm" icon="x" (click)="clearTagFilters()">Clear</button>
          }
        </div>
      }

      @if (retireError()) {
        <div class="card"><div class="card-body"><cf-chip variant="err" dot>{{ retireError() }}</cf-chip></div></div>
      }

      @if (loading()) {
        <div class="card"><div class="card-body muted">Loading roles…</div></div>
      } @else if (error()) {
        <div class="card"><div class="card-body"><cf-chip variant="err" dot>{{ error() }}</cf-chip></div></div>
      } @else if (roles().length === 0) {
        <div class="card"><div class="card-body muted">No roles defined yet. Create one to grant tools to agents.</div></div>
      } @else if (visibleRoles().length === 0) {
        <div class="card"><div class="card-body muted">No roles match the current filters.</div></div>
      } @else {
        <div class="card" style="overflow: hidden">
          <table class="table">
            <thead>
              <tr>
                <th style="width: 42px">
                  <input type="checkbox" [checked]="allVisibleSelected()" (change)="toggleAll($event)" />
                </th>
                <th>Key</th>
                <th>Display name</th>
                <th>Description</th>
                <th>Tags</th>
                <th>Updated</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (role of visibleRoles(); track role.id) {
                <tr [routerLink]="['/settings/roles', role.id]">
                  <td (click)="$event.stopPropagation()">
                    <input type="checkbox" [checked]="isSelected(role.id)" (change)="toggleSelected(role.id, $event)" />
                  </td>
                  <td class="mono" style="font-weight: 500">{{ role.key }}</td>
                  <td>{{ role.displayName }}</td>
                  <td class="muted small">{{ role.description ?? '—' }}</td>
                  <td>
                    <div class="role-tags">
                      @for (tag of visibleRoleTags(role); track tag) {
                        <cf-chip mono>{{ tag }}</cf-chip>
                      }
                      @if (remainingRoleTagCount(role) > 0) {
                        <cf-chip mono>+{{ remainingRoleTagCount(role) }}</cf-chip>
                      }
                      @if (role.tags.length === 0) {
                        <span class="muted small">—</span>
                      }
                    </div>
                  </td>
                  <td class="muted small">{{ role.updatedAtUtc | date:'medium' }}</td>
                  <td class="actions">
                    <button type="button"
                            cf-button
                            size="sm"
                            [routerLink]="['/settings/roles', role.id]"
                            (click)="$event.stopPropagation()">Edit</button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </div>
  `,
  styles: [`
    .tag-filter {
      display: flex;
      align-items: end;
      gap: 8px;
      min-width: min(360px, 100%);
    }
    .tag-filter .field {
      min-width: 180px;
    }
    .tag-filter-input {
      min-height: 34px;
    }
    .tag-filter-row,
    .role-tags {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px;
    }
    .tag-filter-row {
      padding: 2px 0 10px;
    }
    .tag-filter-chip {
      min-height: 28px;
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 4px 9px;
      border-radius: var(--radius-sm);
      border: 1px solid var(--border);
      background: var(--surface);
      color: var(--text-2);
      font: inherit;
      font-family: var(--font-mono);
      font-size: var(--fs-xs);
      cursor: pointer;
    }
    .tag-filter-chip:hover,
    .tag-filter-chip:focus-visible {
      border-color: var(--accent);
      color: var(--accent-ink);
      outline: none;
    }
    .tag-filter-chip.active {
      background: var(--accent-weak);
      border-color: color-mix(in oklab, var(--accent) 35%, transparent);
      color: var(--accent-ink);
    }
    @media (max-width: 760px) {
      .list-toolbar,
      .list-toolbar-left,
      .tag-filter {
        align-items: stretch;
        flex-direction: column;
      }
      .tag-filter .field {
        min-width: 0;
      }
    }
  `],
})
export class RolesListComponent {
  private readonly api = inject(AgentRolesApi);
  private readonly rolesList = useAsyncList(
    () => this.api.list(),
    { errorMessage: 'Failed to load roles' },
  );

  readonly roles = this.rolesList.items;
  readonly loading = this.rolesList.loading;
  readonly error = this.rolesList.error;
  readonly tagFilterInput = signal('');
  readonly selectedTags = signal<string[]>([]);
  readonly selectedIds = signal<Set<number>>(new Set());
  readonly selectedCount = computed(() => this.selectedIds().size);
  readonly visibleRoles = computed(() => {
    const tags = this.selectedTags();
    return this.roles().filter(role =>
      tags.length === 0 || tags.every(tag => hasTag(role, tag)));
  });
  readonly availableTags = computed(() => {
    const seen = new Map<string, string>();
    for (const role of this.roles()) {
      for (const tag of role.tags ?? []) {
        const trimmed = tag.trim();
        if (!trimmed) continue;
        const key = trimmed.toLocaleLowerCase();
        if (!seen.has(key)) {
          seen.set(key, trimmed);
        }
      }
    }
    return [...seen.values()].sort((a, b) => a.localeCompare(b));
  });
  readonly visibleTagOptions = computed(() => {
    const selected = new Set(this.selectedTags().map(tag => tag.toLocaleLowerCase()));
    return this.availableTags()
      .filter(tag => !selected.has(tag.toLocaleLowerCase()))
      .slice(0, 12);
  });
  readonly allVisibleSelected = computed(() =>
    this.visibleRoles().length > 0 && this.visibleRoles().every(role => this.selectedIds().has(role.id))
  );
  readonly retiring = signal(false);
  readonly retireError = signal<string | null>(null);

  constructor() {
    this.rolesList.reload();
  }

  isSelected(id: number): boolean {
    return this.selectedIds().has(id);
  }

  toggleSelected(id: number, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    const next = new Set(this.selectedIds());
    checked ? next.add(id) : next.delete(id);
    this.selectedIds.set(next);
  }

  toggleAll(event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.selectedIds.set(checked ? new Set(this.visibleRoles().map(role => role.id)) : new Set());
  }

  addTagFilter(event?: Event): void {
    event?.preventDefault();
    const tags = parseTagInput(this.tagFilterInput());
    if (tags.length === 0) return;
    for (const tag of tags) {
      this.addTag(tag);
    }
    this.tagFilterInput.set('');
  }

  addTag(tag: string): void {
    const trimmed = tag.trim();
    if (!trimmed) return;
    const next = [...this.selectedTags()];
    if (next.some(existing => existing.localeCompare(trimmed, undefined, { sensitivity: 'accent' }) === 0)) {
      return;
    }
    next.push(trimmed);
    this.selectedTags.set(next);
  }

  removeTagFilter(tag: string): void {
    this.selectedTags.set(this.selectedTags().filter(existing => existing !== tag));
  }

  clearTagFilters(): void {
    this.selectedTags.set([]);
    this.tagFilterInput.set('');
  }

  visibleRoleTags(role: AgentRole): string[] {
    return (role.tags ?? []).slice(0, 4);
  }

  remainingRoleTagCount(role: AgentRole): number {
    return Math.max(0, (role.tags?.length ?? 0) - 4);
  }

  retireSelected(): void {
    const ids = [...this.selectedIds()];
    if (ids.length === 0 || this.retiring()) return;
    this.retiring.set(true);
    this.retireError.set(null);
    this.api.retireMany(ids).subscribe({
      next: () => {
        this.retiring.set(false);
        this.selectedIds.set(new Set());
        this.rolesList.reload();
      },
      error: err => {
        this.retiring.set(false);
        this.retireError.set(formatHttpError(err, 'Failed to retire selected roles.'));
      }
    });
  }
}

function parseTagInput(value: string): string[] {
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

function hasTag(role: AgentRole, tag: string): boolean {
  return (role.tags ?? []).some(existing =>
    existing.localeCompare(tag, undefined, { sensitivity: 'accent' }) === 0);
}
