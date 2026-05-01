import { Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
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
    DatePipe, RouterLink,
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

      @if (retireError()) {
        <div class="card"><div class="card-body"><cf-chip variant="err" dot>{{ retireError() }}</cf-chip></div></div>
      }

      @if (loading()) {
        <div class="card"><div class="card-body muted">Loading roles…</div></div>
      } @else if (error()) {
        <div class="card"><div class="card-body"><cf-chip variant="err" dot>{{ error() }}</cf-chip></div></div>
      } @else if (roles().length === 0) {
        <div class="card"><div class="card-body muted">No roles defined yet. Create one to grant tools to agents.</div></div>
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
                <th>Updated</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (role of roles(); track role.id) {
                <tr [routerLink]="['/settings/roles', role.id]">
                  <td (click)="$event.stopPropagation()">
                    <input type="checkbox" [checked]="isSelected(role.id)" (change)="toggleSelected(role.id, $event)" />
                  </td>
                  <td class="mono" style="font-weight: 500">{{ role.key }}</td>
                  <td>{{ role.displayName }}</td>
                  <td class="muted small">{{ role.description ?? '—' }}</td>
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
  readonly selectedIds = signal<Set<number>>(new Set());
  readonly selectedCount = computed(() => this.selectedIds().size);
  readonly allVisibleSelected = computed(() =>
    this.roles().length > 0 && this.roles().every(role => this.selectedIds().has(role.id))
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
    this.selectedIds.set(checked ? new Set(this.roles().map(role => role.id)) : new Set());
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
