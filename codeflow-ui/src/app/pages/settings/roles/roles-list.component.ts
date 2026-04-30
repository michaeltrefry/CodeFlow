import { Component, inject } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { AgentRolesApi } from '../../../core/agent-roles.api';
import { useAsyncList } from '../../../core/async-state';
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
        <a routerLink="/settings/roles/new">
          <button type="button" cf-button variant="primary" icon="plus">New role</button>
        </a>
      </cf-page-header>

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
                <th>Key</th>
                <th>Display name</th>
                <th>Description</th>
                <th>Updated</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (role of roles(); track role.id) {
                <tr (click)="open(role.id)">
                  <td class="mono" style="font-weight: 500">{{ role.key }}</td>
                  <td>{{ role.displayName }}</td>
                  <td class="muted small">{{ role.description ?? '—' }}</td>
                  <td class="muted small">{{ role.updatedAtUtc | date:'medium' }}</td>
                  <td class="actions">
                    <button type="button" cf-button size="sm">Edit</button>
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
  private readonly router = inject(Router);
  private readonly rolesList = useAsyncList(
    () => this.api.list(),
    { errorMessage: 'Failed to load roles' },
  );

  readonly roles = this.rolesList.items;
  readonly loading = this.rolesList.loading;
  readonly error = this.rolesList.error;

  constructor() {
    this.rolesList.reload();
  }

  open(id: number): void { this.router.navigate(['/settings/roles', id]); }
}
