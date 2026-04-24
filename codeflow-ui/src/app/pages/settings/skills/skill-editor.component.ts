import { Component, inject, input, numberAttribute, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { SkillsApi } from '../../../core/skills.api';
import { Skill } from '../../../core/models';

@Component({
  selector: 'cf-skill-editor',
  standalone: true,
  imports: [FormsModule, RouterLink],
  template: `
    <header class="page-header">
      <div>
        <h1>{{ id() ? 'Edit skill' : 'New skill' }}</h1>
        <p class="muted">Skills granted to an agent's roles are appended to its system prompt at invocation time.</p>
      </div>
      <div class="header-actions">
        @if (id() && !skill()?.isArchived) {
          <button type="button" class="danger" (click)="archive()" [disabled]="saving()">Archive</button>
        }
        <a routerLink="/settings/skills"><button class="secondary">Cancel</button></a>
      </div>
    </header>

    @if (skill()?.isArchived) {
      <p class="tag warn">This skill is archived. It's hidden from agents and from role-grant pickers.</p>
    }

    <form (submit)="submit($event)">
      <div class="form-field">
        <label>Name</label>
        <input [(ngModel)]="name" name="name" required placeholder="socratic-interview" />
      </div>

      <div class="form-field">
        <label>Body</label>
        <textarea [(ngModel)]="body" name="body" rows="18" required></textarea>
        <p class="muted small">
          Use <code>{{ '{{' }}variable.name{{ '}}' }}</code> to reference workflow variables. Unknown variables are left as-is.
        </p>
      </div>

      @if (error()) {
        <div class="tag error">{{ error() }}</div>
      }

      <div class="row" style="margin-top: 1rem;">
        <button type="submit" [disabled]="saving()">
          {{ saving() ? 'Saving…' : (id() ? 'Save changes' : 'Create skill') }}
        </button>
      </div>
    </form>
  `,
  styles: [`
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 1.5rem; gap: 1rem; }
    .header-actions { display: flex; gap: 0.5rem; }
    .muted { color: var(--muted); }
    .small { font-size: 0.8rem; margin-top: 0.25rem; }
    textarea { font-family: var(--font-mono, monospace); font-size: 0.9rem; }
  `]
})
export class SkillEditorComponent implements OnInit {
  private readonly api = inject(SkillsApi);
  private readonly router = inject(Router);

  readonly id = input<number | undefined, unknown>(undefined, {
    transform: (v: unknown) => (v === undefined || v === null || v === '' ? undefined : numberAttribute(v)),
  });

  readonly name = signal('');
  readonly body = signal('');
  readonly skill = signal<Skill | null>(null);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    const existingId = this.id();
    if (existingId) {
      this.api.get(existingId).subscribe(skill => {
        this.skill.set(skill);
        this.name.set(skill.name);
        this.body.set(skill.body);
      });
    }
  }

  submit(event: Event): void {
    event.preventDefault();
    this.saving.set(true);
    this.error.set(null);

    const existingId = this.id();
    if (existingId) {
      this.api.update(existingId, {
        name: this.name(),
        body: this.body(),
      }).subscribe({
        next: skill => {
          this.skill.set(skill);
          this.saving.set(false);
        },
        error: err => {
          this.error.set(this.formatError(err));
          this.saving.set(false);
        }
      });
    } else {
      this.api.create({
        name: this.name(),
        body: this.body(),
      }).subscribe({
        next: skill => {
          this.saving.set(false);
          this.router.navigate(['/settings/skills', skill.id]);
        },
        error: err => {
          this.error.set(this.formatError(err));
          this.saving.set(false);
        }
      });
    }
  }

  archive(): void {
    const existingId = this.id();
    if (!existingId) return;
    if (!confirm('Archive this skill? Agents will stop receiving it immediately.')) return;
    this.saving.set(true);
    this.api.archive(existingId).subscribe({
      next: () => this.router.navigate(['/settings/skills']),
      error: err => {
        this.error.set(this.formatError(err));
        this.saving.set(false);
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
