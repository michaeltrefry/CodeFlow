import { ChangeDetectionStrategy, Component, ElementRef, EventEmitter, Input, Output, ViewChild, booleanAttribute, signal } from '@angular/core';
import { ButtonComponent } from '../button.component';

@Component({
  selector: 'cf-chat-composer',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ButtonComponent],
  template: `
    <form class="composer" (submit)="onSubmit($event)">
      <textarea
        #textarea
        class="composer-input"
        rows="2"
        [placeholder]="placeholder"
        [disabled]="disabled"
        [value]="text()"
        (input)="onInput($event)"
        (keydown)="onKeyDown($event)"
        aria-label="Chat input"
      ></textarea>
      <div class="composer-actions">
        <span class="composer-actions-extras"><ng-content></ng-content></span>
        @if (busy) {
          <cf-button variant="ghost" size="sm" type="button" (click)="cancel.emit()">
            Cancel
          </cf-button>
        }
        <cf-button
          variant="primary"
          size="sm"
          type="submit"
          [disabled]="disabled || !canSend()"
          (click)="onSubmit($event)"
        >
          {{ busy ? 'Streaming…' : 'Send' }}
        </cf-button>
      </div>
    </form>
  `,
  styles: [`
    .composer {
      display: flex;
      flex-direction: column;
      gap: 8px;
      padding: 10px;
      border-top: 1px solid var(--border, rgba(255,255,255,0.08));
      background: var(--surface, #131519);
    }
    .composer-input {
      width: 100%;
      resize: vertical;
      min-height: 44px;
      max-height: 240px;
      padding: 8px 10px;
      border-radius: var(--radius-sm, 6px);
      border: 1px solid var(--border, rgba(255,255,255,0.08));
      background: var(--bg, #0B0C0E);
      color: var(--text, #E7E9EE);
      font: inherit;
      font-size: var(--fs-md, 13px);
    }
    .composer-input:focus {
      outline: none;
      border-color: var(--accent, #5765ff);
    }
    .composer-actions {
      display: flex;
      align-items: center;
      gap: 6px;
    }
    .composer-actions-extras {
      flex: 1 1 auto;
      display: flex;
      align-items: center;
      gap: 8px;
      min-width: 0;
    }
  `],
})
export class ChatComposerComponent {
  @Input() placeholder = 'Ask the CodeFlow assistant anything…';
  @Input({ transform: booleanAttribute }) busy = false;
  @Input({ transform: booleanAttribute }) disabled = false;

  @Output() readonly send = new EventEmitter<string>();
  @Output() readonly cancel = new EventEmitter<void>();

  @ViewChild('textarea', { static: true }) private textareaRef!: ElementRef<HTMLTextAreaElement>;

  protected readonly text = signal('');

  protected canSend(): boolean {
    return !this.busy && this.text().trim().length > 0;
  }

  protected onInput(event: Event): void {
    this.text.set((event.target as HTMLTextAreaElement).value);
  }

  protected onKeyDown(event: KeyboardEvent): void {
    // Enter sends; Shift+Enter inserts newline. Matches the typical chat UX.
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      this.submit();
    }
  }

  protected onSubmit(event: Event): void {
    event.preventDefault();
    this.submit();
  }

  private submit(): void {
    if (!this.canSend()) return;
    const value = this.text().trim();
    this.send.emit(value);
    this.text.set('');
    this.textareaRef.nativeElement.value = '';
  }
}
