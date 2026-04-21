import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { SkillsApi } from '../../../core/skills.api';
import { Skill } from '../../../core/models';

@Component({
  selector: 'cf-skills-list',
  standalone: true,
  imports: [DatePipe, RouterLink],
  template: `
    <header class="page-header">
      <h1>Skills</h1>
      <a routerLink="/settings/skills/new"><button>New skill</button></a>
    </header>

    @if (loading()) {
      <p>Loading skills&hellip;</p>
    } @else if (error()) {
      <p class="tag error">{{ error() }}</p>
    } @else if (skills().length === 0) {
      <p class="tag">No skills defined yet. Create one to extend an agent's system prompt through a role.</p>
    } @else {
      <div class="skill-grid">
        @for (skill of skills(); track skill.id) {
          <a class="card skill-card" [routerLink]="['/settings/skills', skill.id]">
            <div class="skill-header">
              <span class="skill-name">{{ skill.name }}</span>
            </div>
            <p class="skill-preview">{{ preview(skill) }}</p>
            <div class="skill-stamp">
              updated {{ skill.updatedAtUtc | date:'medium' }}
              @if (skill.updatedBy) { by {{ skill.updatedBy }} }
            </div>
          </a>
        }
      </div>
    }
  `,
  styles: [`
    .page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 1.5rem; }
    .skill-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: 1rem; }
    .skill-card { display: block; color: inherit; cursor: pointer; transition: border-color 150ms ease; }
    .skill-card:hover { border-color: var(--color-accent); }
    .skill-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 0.5rem; }
    .skill-name { font-weight: 600; font-size: 1.05rem; font-family: var(--font-mono, monospace); }
    .skill-preview {
      color: var(--color-muted);
      font-size: 0.85rem;
      margin: 0 0 0.5rem;
      display: -webkit-box;
      -webkit-line-clamp: 3;
      -webkit-box-orient: vertical;
      overflow: hidden;
      white-space: pre-wrap;
    }
    .skill-stamp { color: var(--color-muted); font-size: 0.8rem; }
  `]
})
export class SkillsListComponent {
  private readonly api = inject(SkillsApi);

  readonly skills = signal<Skill[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  constructor() {
    this.api.list().subscribe({
      next: skills => {
        this.skills.set(skills);
        this.loading.set(false);
      },
      error: err => {
        this.error.set(err?.message ?? 'Failed to load skills');
        this.loading.set(false);
      }
    });
  }

  preview(skill: Skill): string {
    return skill.body.length > 240 ? skill.body.slice(0, 240) + '…' : skill.body;
  }
}
