import { ChangeDetectionStrategy, Component, EventEmitter, Output, booleanAttribute, input } from '@angular/core';
import { ButtonComponent } from '../button.component';

export interface ChatConfirmationView {
  id: string;
  prompt: string;
  confirmLabel?: string;
  cancelLabel?: string;
  /** True once the user has resolved the chip; UI greys it out and disables both buttons. */
  resolved?: boolean;
  /** True when this confirmation gates a mutating action (run, save, replay-with-edit). */
  destructive?: boolean;
}

/**
 * Inline confirmation primitive used by mutating tool calls (HAA-10 save, HAA-11 run, HAA-13
 * replay-with-edit). HAA-2 ships the shell; tool-call events that surface chips land later.
 */
@Component({
  selector: 'cf-chat-confirmation-chip',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ButtonComponent],
  template: `
    <div class="chip" [attr.data-resolved]="view().resolved ? 'true' : null">
      <p class="chip-prompt">{{ view().prompt }}</p>
      <div class="chip-actions">
        <cf-button
          variant="ghost"
          size="sm"
          type="button"
          [disabled]="view().resolved || disabled()"
          (click)="cancel.emit(view().id)"
        >
          {{ view().cancelLabel ?? 'Cancel' }}
        </cf-button>
        <cf-button
          [variant]="view().destructive ? 'danger' : 'primary'"
          size="sm"
          type="button"
          [disabled]="view().resolved || disabled()"
          (click)="confirm.emit(view().id)"
        >
          {{ view().confirmLabel ?? 'Confirm' }}
        </cf-button>
      </div>
    </div>
  `,
  styles: [`
    .chip {
      display: flex;
      flex-direction: column;
      gap: 8px;
      padding: 10px 12px;
      border-radius: var(--radius-md, 8px);
      border: 1px dashed var(--accent, #5765ff);
      background: color-mix(in oklab, var(--accent, #5765ff) 6%, transparent);
    }
    .chip[data-resolved="true"] {
      opacity: 0.55;
      border-style: solid;
    }
    .chip-prompt {
      margin: 0;
      font-size: var(--fs-md, 13px);
      color: var(--text, #E7E9EE);
    }
    .chip-actions {
      display: flex;
      justify-content: flex-end;
      gap: 6px;
    }
  `],
})
export class ChatConfirmationChipComponent {
  readonly view = input.required<ChatConfirmationView>();
  readonly disabled = input(false, { transform: booleanAttribute });

  @Output() readonly confirm = new EventEmitter<string>();
  @Output() readonly cancel = new EventEmitter<string>();
}
