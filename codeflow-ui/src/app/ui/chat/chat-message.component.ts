import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { renderMarkdown } from './markdown';

export type ChatMessageRole = 'user' | 'assistant' | 'system';

export interface ChatMessageView {
  id: string | null;
  role: ChatMessageRole;
  content: string;
  provider?: string | null;
  model?: string | null;
  /** True while the assistant is still streaming this message. */
  pending?: boolean;
}

@Component({
  selector: 'cf-chat-message',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <article class="chat-msg" [attr.data-role]="message().role">
      <header class="chat-msg-head">
        <span class="chat-msg-role">{{ roleLabel() }}</span>
        @if (message().pending) {
          <span class="chat-msg-pending" aria-live="polite">streaming…</span>
        }
        @if (message().provider && !message().pending) {
          <span class="chat-msg-meta">
            {{ message().provider }} · {{ message().model }}
          </span>
        }
      </header>
      @if (message().role === 'user') {
        <div class="chat-msg-body chat-msg-body--plain">{{ message().content }}</div>
      } @else {
        <div class="chat-msg-body chat-msg-body--markdown" [innerHTML]="renderedHtml()"></div>
      }
    </article>
  `,
  styles: [`
    .chat-msg {
      display: flex;
      flex-direction: column;
      gap: 6px;
      padding: 10px 12px;
      border-radius: var(--radius-md, 8px);
      background: var(--surface, #131519);
      border: 1px solid var(--border, rgba(255,255,255,0.08));
    }
    .chat-msg[data-role="user"] {
      background: color-mix(in oklab, var(--accent, #5765ff) 10%, transparent);
      border-color: color-mix(in oklab, var(--accent, #5765ff) 25%, transparent);
    }
    .chat-msg-head {
      display: flex;
      gap: 10px;
      align-items: baseline;
      font-size: var(--fs-sm, 12px);
    }
    .chat-msg-role {
      text-transform: uppercase;
      letter-spacing: 0.06em;
      font-weight: 600;
      color: var(--text-muted, #9aa3b2);
    }
    .chat-msg-pending {
      color: var(--text-muted, #9aa3b2);
      font-style: italic;
    }
    .chat-msg-meta {
      margin-left: auto;
      color: var(--text-muted, #9aa3b2);
      font-size: 11px;
      font-variant-numeric: tabular-nums;
    }
    .chat-msg-body {
      font-size: var(--fs-md, 13px);
      line-height: 1.5;
      color: var(--text, #E7E9EE);
      word-wrap: break-word;
    }
    .chat-msg-body--plain {
      white-space: pre-wrap;
    }
    .chat-msg-body--markdown :is(p, ul, ol, pre, blockquote) {
      margin: 0 0 10px 0;
    }
    .chat-msg-body--markdown :is(p, ul, ol, pre, blockquote):last-child {
      margin-bottom: 0;
    }
    .chat-msg-body--markdown pre {
      background: var(--bg, #0B0C0E);
      border: 1px solid var(--border, rgba(255,255,255,0.08));
      border-radius: var(--radius-sm, 6px);
      padding: 8px 10px;
      overflow-x: auto;
      font-family: var(--font-mono, ui-monospace, SFMono-Regular, monospace);
      font-size: 12px;
    }
    .chat-msg-body--markdown code {
      font-family: var(--font-mono, ui-monospace, SFMono-Regular, monospace);
      font-size: 0.92em;
      background: color-mix(in oklab, var(--text, #E7E9EE) 8%, transparent);
      padding: 1px 4px;
      border-radius: 3px;
    }
    .chat-msg-body--markdown pre code {
      background: none;
      padding: 0;
    }
    .chat-msg-body--markdown a {
      color: var(--accent, #6ea8fe);
    }
    /* HAA-9: assistant-emitted workflow package preview. Summary line on top, JSON in a
       collapsed <details>. Uses the standard chat code-block treatment for the JSON. */
    .chat-msg-body--markdown .cf-workflow-package {
      border: 1px solid var(--border, rgba(255,255,255,0.10));
      border-radius: var(--radius-sm, 6px);
      background: color-mix(in oklab, var(--accent, #5765ff) 6%, transparent);
      padding: 8px 10px;
      margin: 0 0 10px 0;
    }
    .chat-msg-body--markdown .cf-workflow-package-summary {
      font-size: var(--fs-sm, 12px);
      color: var(--text, #E7E9EE);
      margin-bottom: 6px;
    }
    .chat-msg-body--markdown .cf-workflow-package-summary code {
      background: color-mix(in oklab, var(--text, #E7E9EE) 10%, transparent);
    }
    .chat-msg-body--markdown .cf-workflow-package-detail summary {
      cursor: pointer;
      font-size: 11px;
      color: var(--text-muted, #9aa3b2);
      padding: 2px 0;
      user-select: none;
    }
    .chat-msg-body--markdown .cf-workflow-package-detail summary:hover {
      color: var(--text, #E7E9EE);
    }
    .chat-msg-body--markdown .cf-workflow-package-detail pre {
      margin: 6px 0 0 0;
    }
  `],
})
export class ChatMessageComponent {
  readonly message = input.required<ChatMessageView>();

  readonly renderedHtml = computed(() => renderMarkdown(this.message().content));

  protected roleLabel(): string {
    switch (this.message().role) {
      case 'user': return 'You';
      case 'assistant': return 'Assistant';
      case 'system': return 'System';
    }
  }
}
