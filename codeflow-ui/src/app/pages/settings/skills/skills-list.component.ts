import { Component, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { SkillsApi } from '../../../core/skills.api';
import { Skill } from '../../../core/models';
import { PageHeaderComponent } from '../../../ui/page-header.component';
import { ButtonComponent } from '../../../ui/button.component';
import { ChipComponent } from '../../../ui/chip.component';

function relTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return '—';
  const diff = (Date.now() - t) / 1000;
  if (diff < 60) return `${Math.max(0, Math.round(diff))}s ago`;
  if (diff < 3600) return `${Math.round(diff / 60)}m ago`;
  if (diff < 86400) return `${Math.round(diff / 3600)}h ago`;
  return `${Math.round(diff / 86400)}d ago`;
}

@Component({
  selector: 'cf-skills-list',
  standalone: true,
  imports: [
    RouterLink,
    PageHeaderComponent, ButtonComponent, ChipComponent,
  ],
  template: `
    <div class="page">
      <cf-page-header
        title="Skills"
        subtitle="Reusable policy snippets. Composed into agent system prompts at build time.">
        <a routerLink="/settings/skills/new">
          <button type="button" cf-button variant="primary" icon="plus">New skill</button>
        </a>
      </cf-page-header>

      @if (loading()) {
        <div class="card"><div class="card-body muted">Loading skills…</div></div>
      } @else if (error()) {
        <div class="card"><div class="card-body"><cf-chip variant="err" dot>{{ error() }}</cf-chip></div></div>
      } @else if (skills().length === 0) {
        <div class="card"><div class="card-body muted">No skills defined yet. Create one to extend an agent's system prompt through a role.</div></div>
      } @else {
        <div class="card" style="overflow: hidden">
          <div class="skill-list">
            @for (skill of skills(); track skill.id) {
              <a class="skill-list-item" [routerLink]="['/settings/skills', skill.id]">
                <div class="mono" style="font-weight: 500">{{ skill.name }}</div>
                <div class="muted small" style="margin-top: 2px">updated {{ relTime(skill.updatedAtUtc) }}</div>
                <p class="muted small" style="margin-top: 6px">{{ preview(skill) }}</p>
              </a>
            }
          </div>
        </div>
      }
    </div>
  `,
  styles: [`
    a.skill-list-item { display: block; text-decoration: none; color: inherit; }
    a.skill-list-item p { display: -webkit-box; -webkit-line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden; white-space: pre-wrap; }
  `]
})
export class SkillsListComponent {
  private readonly api = inject(SkillsApi);
  private readonly router = inject(Router);

  readonly skills = signal<Skill[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  constructor() {
    this.api.list().subscribe({
      next: skills => { this.skills.set(skills); this.loading.set(false); },
      error: err => {
        this.error.set(err?.message ?? 'Failed to load skills');
        this.loading.set(false);
      },
    });
  }

  preview(skill: Skill): string {
    return skill.body.length > 240 ? skill.body.slice(0, 240) + '…' : skill.body;
  }

  relTime = relTime;
}
