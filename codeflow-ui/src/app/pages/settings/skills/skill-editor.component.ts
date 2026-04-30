import { Component, inject, input, numberAttribute, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { formatHttpError } from '../../../core/format-error';
import { SkillsApi } from '../../../core/skills.api';
import { Skill } from '../../../core/models';
import { PageHeaderComponent } from '../../../ui/page-header.component';
import { ButtonComponent } from '../../../ui/button.component';
import { ChipComponent } from '../../../ui/chip.component';
import { CardComponent } from '../../../ui/card.component';

@Component({
  selector: 'cf-skill-editor',
  standalone: true,
  imports: [
    FormsModule, RouterLink,
    PageHeaderComponent, ButtonComponent, ChipComponent, CardComponent,
  ],
  template: `
    <div class="page">
    <cf-page-header
      [title]="id() ? 'Edit skill' : 'New skill'"
      subtitle="Skills granted to an agent's roles are appended to its system prompt at invocation time.">
      @if (id() && !skill()?.isArchived) {
        <button type="button" cf-button variant="danger" icon="trash" (click)="archive()" [disabled]="saving()">Archive</button>
      }
      <a routerLink="/settings/skills">
        <button type="button" cf-button variant="ghost" icon="back">Cancel</button>
      </a>
      <button type="button" cf-button variant="primary" icon="check" (click)="submit($event)" [disabled]="saving()">
        {{ saving() ? 'Saving…' : (id() ? 'Save changes' : 'Create skill') }}
      </button>
    </cf-page-header>

    @if (skill()?.isArchived) {
      <cf-chip variant="warn" dot>This skill is archived. It's hidden from agents and from role-grant pickers.</cf-chip>
    }

    <form (submit)="submit($event)">
      <cf-card title="Skill definition">
        <div class="stack">
          <label class="field">
            <span class="field-label">Name</span>
            <input class="input mono" [(ngModel)]="name" name="name" required placeholder="socratic-interview" />
          </label>

          <label class="field">
            <span class="field-label">Body</span>
            <div class="code-field">
              <div class="code-field-head"><span>{{ name() || 'skill' }}.md</span><span>markdown</span></div>
              <textarea class="textarea mono" rows="18" required
                        [(ngModel)]="body" name="body"
                        style="border: 0; border-radius: 0; background: var(--bg)"></textarea>
            </div>
            <span class="field-hint">
              Use <code>{{ '{{' }}variable.name{{ '}}' }}</code> to reference workflow variables. Unknown variables are left as-is.
            </span>
          </label>
        </div>

        @if (error()) {
          <div class="trace-failure" style="margin-top: 10px"><strong>Save failed:</strong> {{ error() }}</div>
        }
      </cf-card>
    </form>
    </div>
  `,
  styles: [``]
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
          this.error.set(formatHttpError(err, 'Save failed'));
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
          this.error.set(formatHttpError(err, 'Save failed'));
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
        this.error.set(formatHttpError(err, 'Save failed'));
        this.saving.set(false);
      }
    });
  }

}
