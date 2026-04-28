import { ChangeDetectionStrategy, Component, booleanAttribute, input, signal } from '@angular/core';

export type ChatToolCallStatus = 'pending' | 'success' | 'error';

export interface ChatToolCallView {
  id: string;
  name: string;
  status: ChatToolCallStatus;
  argsPreview?: string;
  resultPreview?: string;
  errorMessage?: string;
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
  template: `
    <details class="tool-call" [attr.data-status]="view().status" [open]="defaultOpen()">
      <summary class="tool-call-head">
        <span class="tool-call-status" [attr.data-status]="view().status" aria-hidden="true"></span>
        <span class="tool-call-name">{{ view().name }}</span>
        <span class="tool-call-status-label">{{ statusLabel() }}</span>
      </summary>
      <div class="tool-call-body">
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
    @keyframes pulse { 0%,100% { opacity: 1; } 50% { opacity: 0.4; } }
  `],
})
export class ChatToolCallComponent {
  readonly view = input.required<ChatToolCallView>();
  readonly defaultOpen = input(false, { transform: booleanAttribute });

  protected statusLabel(): string {
    switch (this.view().status) {
      case 'pending': return 'Running';
      case 'success': return 'Success';
      case 'error':   return 'Error';
    }
  }

  // Keep a no-op signal reference so future expansion (loading-state animations, copy-result
  // affordances) doesn't have to re-plumb the host into a stateful component.
  protected readonly _internal = signal<unknown>(null);
}
