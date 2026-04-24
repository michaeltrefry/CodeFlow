import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  ViewChild,
  computed,
  input,
  output,
  signal
} from '@angular/core';
import { FormsModule } from '@angular/forms';

/**
 * Multi-chip text input with autocomplete suggestions and a hard limit on the
 * number of tags. Used in the workflow editor (capped at 5) and reused by the
 * workflow list's tag-search box (uncapped by passing maxTags=0).
 */
@Component({
  selector: 'cf-tag-input',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tag-input" [class.is-disabled]="disabled()">
      @for (tag of tags(); track tag) {
        <span class="tag-chip">
          <span class="tag-text">{{ tag }}</span>
          @if (!disabled()) {
            <button type="button" class="tag-remove" (click)="remove(tag)" aria-label="Remove tag">×</button>
          }
        </span>
      }
      @if (canAddMore()) {
        <input
          #textInput
          type="text"
          class="tag-field"
          [placeholder]="placeholderLabel()"
          [(ngModel)]="draft"
          (keydown)="onKeyDown($event)"
          (focus)="focused.set(true)"
          (blur)="onBlur()"
          [disabled]="disabled()"
          autocomplete="off" />
      }
      @if (showSuggestions()) {
        <ul class="tag-menu" (mousedown)="$event.preventDefault()">
          @for (suggestion of filteredSuggestions(); track suggestion) {
            <li class="tag-menu-item" (click)="pickSuggestion(suggestion)">{{ suggestion }}</li>
          }
        </ul>
      }
    </div>
    @if (maxTags() > 0 && showCounter()) {
      <div class="tag-meta muted xsmall">{{ tags().length }} / {{ maxTags() }}</div>
    }
  `,
  styles: [`
    :host { display: block; }
    .tag-input {
      position: relative;
      display: flex;
      flex-wrap: wrap;
      gap: 0.3rem;
      align-items: center;
      padding: 0.25rem 0.4rem;
      border-radius: 6px;
      border: 1px solid var(--border);
      background: var(--surface);
      min-height: 32px;
    }
    .tag-input.is-disabled { opacity: 0.6; }
    .tag-chip {
      display: inline-flex;
      align-items: center;
      gap: 0.25rem;
      padding: 0.15rem 0.45rem;
      background: color-mix(in oklab, var(--accent) 18%, transparent);
      color: var(--accent);
      border: 1px solid color-mix(in oklab, var(--accent) 40%, transparent);
      border-radius: 4px;
      font-size: 0.75rem;
      font-weight: 500;
      white-space: nowrap;
    }
    .tag-text { line-height: 1; }
    .tag-remove {
      appearance: none;
      border: none;
      background: transparent;
      color: inherit;
      padding: 0;
      margin-left: 0.15rem;
      font-size: 0.95rem;
      line-height: 0.8;
      cursor: pointer;
      opacity: 0.7;
    }
    .tag-remove:hover { opacity: 1; }
    .tag-field {
      flex: 1;
      min-width: 110px;
      border: none;
      outline: none;
      background: transparent;
      color: inherit;
      font: inherit;
      padding: 0.2rem 0.25rem;
    }
    .tag-menu {
      position: absolute;
      top: calc(100% + 4px);
      left: 0;
      right: 0;
      z-index: 30;
      list-style: none;
      margin: 0;
      padding: 0.25rem;
      border: 1px solid var(--border);
      background: var(--surface-2, var(--surface));
      border-radius: 6px;
      max-height: 180px;
      overflow-y: auto;
      box-shadow: 0 8px 24px rgba(0, 0, 0, 0.2);
    }
    .tag-menu-item {
      padding: 0.3rem 0.5rem;
      border-radius: 4px;
      cursor: pointer;
      font-size: 0.8rem;
    }
    .tag-menu-item:hover {
      background: color-mix(in oklab, var(--accent) 15%, transparent);
      color: var(--accent);
    }
    .tag-meta { margin-top: 0.3rem; }
    .muted { color: var(--muted); }
    .xsmall { font-size: 0.72rem; }
  `]
})
export class TagInputComponent {
  readonly tags = input<string[]>([]);
  readonly suggestions = input<string[]>([]);
  readonly maxTags = input<number>(0);
  readonly placeholder = input<string>('Add tag…');
  readonly disabled = input<boolean>(false);
  readonly showCounter = input<boolean>(true);

  readonly tagsChange = output<string[]>();

  @ViewChild('textInput') textInput?: ElementRef<HTMLInputElement>;

  readonly draft = signal<string>('');
  readonly focused = signal<boolean>(false);

  readonly canAddMore = computed(() => {
    const max = this.maxTags();
    return max === 0 || this.tags().length < max;
  });

  readonly filteredSuggestions = computed(() => {
    const query = this.draft().trim().toLowerCase();
    const existing = new Set(this.tags().map(t => t.toLowerCase()));
    return this.suggestions()
      .filter(s => !existing.has(s.toLowerCase()))
      .filter(s => query.length === 0 || s.toLowerCase().includes(query))
      .slice(0, 8);
  });

  readonly showSuggestions = computed(() =>
    this.focused() && this.canAddMore() && this.filteredSuggestions().length > 0);

  readonly placeholderLabel = computed(() =>
    this.tags().length === 0 ? this.placeholder() : '');

  onKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Enter' || event.key === ',' || event.key === 'Tab') {
      const value = this.draft().trim();
      if (value.length > 0) {
        event.preventDefault();
        this.commit(value);
      }
    } else if (event.key === 'Backspace' && this.draft().length === 0 && this.tags().length > 0) {
      event.preventDefault();
      this.remove(this.tags()[this.tags().length - 1]);
    } else if (event.key === 'Escape') {
      this.draft.set('');
      this.focused.set(false);
    }
  }

  pickSuggestion(suggestion: string): void {
    this.commit(suggestion);
    this.textInput?.nativeElement.focus();
  }

  commit(value: string): void {
    const normalized = value.trim();
    if (normalized.length === 0) return;

    const existing = this.tags();
    if (existing.some(t => t.toLowerCase() === normalized.toLowerCase())) {
      this.draft.set('');
      return;
    }

    const max = this.maxTags();
    if (max > 0 && existing.length >= max) {
      return;
    }

    this.tagsChange.emit([...existing, normalized]);
    this.draft.set('');
  }

  remove(tag: string): void {
    const next = this.tags().filter(t => t !== tag);
    this.tagsChange.emit(next);
  }

  onBlur(): void {
    // Delay so suggestion-item mousedown can still register before we hide the menu.
    setTimeout(() => this.focused.set(false), 120);
  }
}
