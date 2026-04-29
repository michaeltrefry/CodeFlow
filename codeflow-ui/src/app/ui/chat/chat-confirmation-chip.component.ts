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
    <div class="confirmation-chip" [attr.data-resolved]="view().resolved ? 'true' : null">
      <p class="confirmation-chip-prompt">{{ view().prompt }}</p>
      <div class="confirmation-chip-actions">
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
    /* Custom elements default to display: inline, which collapses the host to text-line height
       and lets the inner flex column overflow the host's box. The chip's bottom border then
       slices across the prompt text. Lock the host to block so it sizes to its content. */
    :host {
      display: block;
    }
    /* Match the workspace-switch prompt (chat-panel.ws-prompt) so all confirmation surfaces in
       the assistant read the same way: bright solid border, bolder prompt, slide-in on enter so
       the user notices the chip even when they were scrolled mid-thread. The 2px border + soft
       outer glow are deliberate — these chips gate mutating actions, so visual weight is the
       point. */
    .confirmation-chip {
      display: flex;
      flex-direction: column;
      gap: 10px;
      padding: 12px 14px;
      border-radius: var(--radius-md, 8px);
      border: 2px solid var(--accent, #5765ff);
      background: color-mix(in oklab, var(--accent, #5765ff) 12%, var(--surface, #131519));
      box-shadow: 0 0 0 4px color-mix(in oklab, var(--accent, #5765ff) 18%, transparent);
      box-sizing: border-box;
      animation: chip-slide-up 180ms ease-out;
    }
    @keyframes chip-slide-up {
      from { transform: translateY(8px); opacity: 0; }
      to   { transform: translateY(0);    opacity: 1; }
    }
    .confirmation-chip[data-resolved="true"] {
      opacity: 0.55;
      border-style: dashed;
      border-width: 1px;
      box-shadow: none;
      animation: none;
    }
    .confirmation-chip-prompt {
      margin: 0;
      font-size: var(--fs-md, 13px);
      font-weight: 600;
      line-height: 1.3;
      color: var(--text, #E7E9EE);
      overflow-wrap: anywhere;
    }
    .confirmation-chip-actions {
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
