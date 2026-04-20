import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { AgentRolesApi } from '../../../core/agent-roles.api';
import { AgentRole } from '../../../core/models';

@Component({
  selector: 'cf-roles-list',
  standalone: true,
  imports: [DatePipe, RouterLink],
  template: `
    <header class="page-header">
      <h1>Roles</h1>
      <a routerLink="/settings/roles/new"><button>New role</button></a>
    </header>

    @if (loading()) {
      <p>Loading roles&hellip;</p>
    } @else if (error()) {
      <p class="tag error">{{ error() }}</p>
    } @else if (roles().length === 0) {
      <p class="tag">No roles defined yet. Create one to grant tools to agents.</p>
    } @else {
      <div class="role-grid">
        @for (role of roles(); track role.id) {
          <a class="card role-card" [routerLink]="['/settings/roles', role.id]">
            <div class="role-header">
              <span class="role-key">{{ role.key }}</span>
              <span class="tag accent">{{ role.displayName }}</span>
            </div>
            @if (role.description) {
              <p class="role-desc">{{ role.description }}</p>
            }
            <div class="role-stamp">
              updated {{ role.updatedAtUtc | date:'medium' }}
              @if (role.updatedBy) { by {{ role.updatedBy }} }
            </div>
          </a>
        }
      </div>
    }
  `,
  styles: [`
    .page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 1.5rem; }
    .role-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: 1rem; }
    .role-card { display: block; color: inherit; cursor: pointer; transition: border-color 150ms ease; }
    .role-card:hover { border-color: var(--color-accent); }
    .role-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 0.5rem; }
    .role-key { font-weight: 600; font-size: 1.05rem; font-family: var(--font-mono, monospace); }
    .role-desc { color: var(--color-muted); font-size: 0.9rem; margin: 0 0 0.5rem; }
    .role-stamp { color: var(--color-muted); font-size: 0.8rem; }
  `]
})
export class RolesListComponent {
  private readonly api = inject(AgentRolesApi);

  readonly roles = signal<AgentRole[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  constructor() {
    this.api.list().subscribe({
      next: roles => {
        this.roles.set(roles);
        this.loading.set(false);
      },
      error: err => {
        this.error.set(err?.message ?? 'Failed to load roles');
        this.loading.set(false);
      }
    });
  }
}
