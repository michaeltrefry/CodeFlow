import { ChangeDetectionStrategy, Component, EventEmitter, Output, booleanAttribute, input, signal } from '@angular/core';
import { ChatConfirmationChipComponent, ChatConfirmationView } from './chat-confirmation-chip.component';

export type ChatToolCallStatus = 'pending' | 'success' | 'error';

/**
 * HAA-10 / HAA-11: confirmation state attached to a tool call when the tool's verdict requires
 * the user to authorize a follow-on mutating action. Rendered as an inline
 * {@link ChatConfirmationChipComponent} inside the tool-call body. The chat-panel owns the API
 * call when the user confirms; this component just surfaces the chip and emits confirm/cancel.
 *
 * The `kind` discriminator + per-kind `applied` shape lets the chip display a tool-specific
 * success banner (library link for save, trace link for run) without leaking either tool's
 * domain into the chip primitive.
 */
export interface ChatToolCallConfirmation {
  kind: 'save_workflow_package' | 'run_workflow' | 'propose_replay_with_edit';
  prompt: string;
  confirmLabel?: string;
  cancelLabel?: string;
  /**
   * 'idle' = chip presented to user; 'applying' = confirm clicked, awaiting API; 'success' /
   * 'error' = terminal. The chip itself just disables on `applying`/`success`/`error`; the
   * banner below the chip surfaces the resolution.
   */
  state: 'idle' | 'applying' | 'success' | 'error' | 'cancelled';
  /**
   * When state === 'success', the per-tool result payload used to render the success banner.
   * Save: `{ kind: 'workflow', key, version }`. Run: `{ kind: 'trace', traceId }`.
   * Replay (HAA-13): `{ kind: 'replay', originalTraceId, replayState, replayTerminalPort? }`.
   */
  applied?:
    | { kind: 'workflow'; key: string; version: number }
    | { kind: 'trace'; traceId: string }
    | { kind: 'replay'; originalTraceId: string; replayState: string; replayTerminalPort?: string | null };
  /** When state === 'error', a human-readable error message. */
  errorMessage?: string;
}

export interface ChatToolCallView {
  id: string;
  name: string;
  status: ChatToolCallStatus;
  argsPreview?: string;
  resultPreview?: string;
  errorMessage?: string;
  confirmation?: ChatToolCallConfirmation;
}

/**
 * Collapsed call → result frame for an assistant tool invocation. HAA-2 ships the shape; the
 * concrete tool-call events that drive it land in HAA-4 / HAA-5 / HAA-9 / HAA-10 / HAA-11 /
 * HAA-12 as each tool surface comes online.
 */
@Component({
  selector: 'cf-chat-tool-call',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChatConfirmationChipComponent],
  template: `
    <details class="tool-call" [attr.data-status]="view().status" [open]="isOpenByDefault()">
      <summary class="tool-call-head">
        <span class="tool-call-status" [attr.data-status]="view().status" aria-hidden="true"></span>
        <span class="tool-call-name">{{ view().name }}</span>
        <span class="tool-call-status-label">{{ statusLabel() }}</span>
      </summary>
      <div class="tool-call-body">
        @if (view().confirmation; as confirmation) {
          <section class="tool-call-section tool-call-confirm-section" data-testid="tool-confirmation">
            <cf-chat-confirmation-chip
              [view]="chipView()"
              [disabled]="confirmation.state === 'applying'"
              (confirm)="onConfirm()"
              (cancel)="onCancel()"
            />
            @if (confirmation.state === 'applying') {
              <p class="tool-confirmation-status">Saving…</p>
            }
            @if (confirmation.state === 'success' && confirmation.applied; as applied) {
              <p class="tool-confirmation-status tool-confirmation-success">
                @switch (applied.kind) {
                  @case ('workflow') {
                    Saved as
                    <a [href]="'/workflows/' + applied.key + '/' + applied.version">
                      {{ applied.key }} v{{ applied.version }}
                    </a>.
                  }
                  @case ('trace') {
                    Started trace
                    <a [href]="'/traces/' + applied.traceId">{{ applied.traceId.slice(0, 8) }}…</a>.
                  }
                  @case ('replay') {
                    Replay {{ replayStateLabel(applied.replayState) }}
                    @if (applied.replayTerminalPort) {
                      via <code>{{ applied.replayTerminalPort }}</code>
                    }
                    — open the
                    <a [href]="'/traces/' + applied.originalTraceId">trace inspector</a>
                    to review or refine in Replay-with-Edit.
                  }
                }
              </p>
            }
            @if (confirmation.state === 'error' && confirmation.errorMessage) {
              <p class="tool-confirmation-status tool-confirmation-error">{{ confirmation.errorMessage }}</p>
            }
            @if (confirmation.state === 'cancelled') {
              <p class="tool-confirmation-status">Cancelled.</p>
            }
          </section>
        }
        @if (view().argsPreview) {
          <section class="tool-call-section">
            <h4>Arguments</h4>
            <pre>{{ view().argsPreview }}</pre>
          </section>
        }
        @if (view().status === 'error' && view().errorMessage) {
          <section class="tool-call-section">
            <h4>Error</h4>
            <pre class="tool-call-error">{{ view().errorMessage }}</pre>
          </section>
        } @else if (view().resultPreview) {
          <section class="tool-call-section">
            <h4>Result</h4>
            <pre>{{ view().resultPreview }}</pre>
          </section>
        }
      </div>
    </details>
  `,
  styles: [`
    .tool-call {
      border: 1px solid var(--border, rgba(255,255,255,0.08));
      border-radius: var(--radius-sm, 6px);
      background: var(--bg, #0B0C0E);
      font-size: var(--fs-sm, 12px);
    }
    .tool-call-head {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 6px 10px;
      cursor: pointer;
      user-select: none;
    }
    .tool-call-status {
      display: inline-block;
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--text-muted, #9aa3b2);
    }
    .tool-call-status[data-status="pending"] { background: var(--sem-amber, #d29922); animation: pulse 1.2s ease-in-out infinite; }
    .tool-call-status[data-status="success"] { background: var(--sem-green, #3FB950); }
    .tool-call-status[data-status="error"]   { background: var(--sem-red, #f85149); }
    .tool-call-name {
      font-family: var(--font-mono, ui-monospace, SFMono-Regular, monospace);
      color: var(--text, #E7E9EE);
    }
    .tool-call-status-label {
      margin-left: auto;
      color: var(--text-muted, #9aa3b2);
      text-transform: uppercase;
      letter-spacing: 0.06em;
      font-size: 10px;
    }
    .tool-call-body {
      padding: 6px 10px 10px;
      display: flex;
      flex-direction: column;
      gap: 8px;
      border-top: 1px solid var(--border, rgba(255,255,255,0.08));
    }
    .tool-call-section h4 {
      margin: 0 0 4px 0;
      font-size: 10px;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      color: var(--text-muted, #9aa3b2);
    }
    .tool-call-section pre {
      margin: 0;
      padding: 6px 8px;
      background: var(--surface, #131519);
      border-radius: 4px;
      font-family: var(--font-mono, ui-monospace, SFMono-Regular, monospace);
      font-size: 11px;
      overflow-x: auto;
    }
    .tool-call-error {
      color: var(--sem-red, #f85149);
    }
    /* When a confirmation chip is present, give it the room a 2px-bordered + glowing chip needs
       and put a hairline underneath so the Arguments/Result sections below visually separate. */
    .tool-call-confirm-section {
      padding: 4px 0 12px;
      margin-bottom: 4px;
      border-bottom: 1px dashed var(--border, rgba(255,255,255,0.08));
    }
    .tool-confirmation-status {
      margin: 6px 0 0 0;
      font-size: 11px;
      color: var(--text-muted, #9aa3b2);
    }
    .tool-confirmation-success { color: var(--sem-green, #3FB950); }
    .tool-confirmation-error   { color: var(--sem-red, #f85149); }
    @keyframes pulse { 0%,100% { opacity: 1; } 50% { opacity: 0.4; } }
  `],
})
export class ChatToolCallComponent {
  readonly view = input.required<ChatToolCallView>();
  readonly defaultOpen = input(false, { transform: booleanAttribute });

  /** Emitted when the user clicks the confirmation chip's primary action. */
  @Output() readonly confirmConfirmation = new EventEmitter<string>();
  /** Emitted when the user clicks the confirmation chip's cancel action. */
  @Output() readonly cancelConfirmation = new EventEmitter<string>();

  protected statusLabel(): string {
    switch (this.view().status) {
      case 'pending': return 'Running';
      case 'success': return 'Success';
      case 'error':   return 'Error';
    }
  }

  /**
   * HAA-13: humanize the dry-run executor's terminal-state strings for the replay success
   * banner. Falls through to the raw value if the server adds a state we haven't surfaced yet.
   */
  protected replayStateLabel(state: string): string {
    switch (state) {
      case 'Completed':           return 'completed';
      case 'Failed':              return 'failed';
      case 'HitlReached':         return 'paused at HITL';
      case 'StepLimitExceeded':   return 'hit the step limit';
      case 'DriftRefused':        return 'refused due to drift';
      default:                    return state.toLowerCase();
    }
  }

  /**
   * Auto-expand when there's a confirmation chip so the user sees it without clicking the
   * collapsed disclosure. Otherwise honor the host's `defaultOpen` input.
   */
  protected isOpenByDefault(): boolean {
    return this.defaultOpen() || !!this.view().confirmation;
  }

  protected chipView(): ChatConfirmationView {
    const conf = this.view().confirmation!;
    return {
      id: this.view().id,
      prompt: conf.prompt,
      confirmLabel: conf.confirmLabel,
      cancelLabel: conf.cancelLabel,
      destructive: true,
      resolved: conf.state !== 'idle',
    };
  }

  protected onConfirm(): void {
    this.confirmConfirmation.emit(this.view().id);
  }

  protected onCancel(): void {
    this.cancelConfirmation.emit(this.view().id);
  }

  // Keep a no-op signal reference so future expansion (loading-state animations, copy-result
  // affordances) doesn't have to re-plumb the host into a stateful component.
  protected readonly _internal = signal<unknown>(null);
}
