import { ChangeDetectionStrategy, Component, ElementRef, HostListener, computed, inject, input, output, signal } from '@angular/core';
import { IconComponent } from '../icon.component';
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
  imports: [IconComponent],
  template: `
    <article class="chat-msg" [attr.data-role]="message().role">
      <header class="chat-msg-head">
        <span class="chat-msg-role">{{ roleLabel() }}</span>
        @if (message().pending) {
          <span class="chat-msg-pending" aria-live="polite">
            <span class="chat-msg-spinner" aria-hidden="true"></span>
            <span>streaming…</span>
          </span>
        }
        @if (message().provider && !message().pending) {
          <span class="chat-msg-meta">
            {{ message().provider }} · {{ message().model }}
          </span>
        }
        @if (showActions()) {
          <div class="chat-msg-actions">
            <button
              type="button"
              class="chat-msg-action"
              [attr.aria-label]="copied() ? 'Copied' : 'Copy message'"
              [title]="copied() ? 'Copied' : 'Copy message'"
              (click)="onCopyClick()"
            >
              <cf-icon [name]="copied() ? 'check' : 'copy'"></cf-icon>
              @if (copied()) {
                <span class="chat-msg-tooltip" role="status">Copied</span>
              }
            </button>
            <button
              type="button"
              class="chat-msg-action"
              aria-label="Fork conversation from here"
              title="Fork conversation from here"
              [disabled]="!message().id"
              (click)="onForkClick()"
            >
              <cf-icon name="fork"></cf-icon>
            </button>
          </div>
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
      align-items: center;
      font-size: var(--fs-sm, 12px);
    }
    .chat-msg-role {
      text-transform: uppercase;
      letter-spacing: 0.06em;
      font-weight: 600;
      color: var(--text-muted, #9aa3b2);
    }
    .chat-msg-pending {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      color: var(--text-muted, #9aa3b2);
      font-style: italic;
    }
    .chat-msg-spinner {
      width: 10px;
      height: 10px;
      border-radius: 999px;
      border: 2px solid color-mix(in oklab, var(--text-muted, #9aa3b2) 35%, transparent);
      border-top-color: var(--accent, #6ea8fe);
      flex: 0 0 auto;
      animation: chat-msg-spin 0.75s linear infinite;
    }
    @keyframes chat-msg-spin {
      to { transform: rotate(360deg); }
    }
    @media (prefers-reduced-motion: reduce) {
      .chat-msg-spinner {
        animation: none;
        border-color: var(--accent, #6ea8fe);
      }
    }
    .chat-msg-meta {
      margin-left: auto;
      color: var(--text-muted, #9aa3b2);
      font-size: 11px;
      font-variant-numeric: tabular-nums;
    }
    /* Per-message actions (copy / fork). Sit at the right edge of the header strip; sized to
       blend in next to the role + provider/model meta line so they read as ambient affordances
       rather than primary controls. */
    .chat-msg-actions {
      display: inline-flex;
      gap: 2px;
      margin-left: auto;
      align-items: center;
    }
    .chat-msg-meta + .chat-msg-actions {
      margin-left: 6px;
    }
    .chat-msg-action {
      position: relative;
      appearance: none;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 22px;
      height: 22px;
      padding: 0;
      border: 1px solid transparent;
      border-radius: 4px;
      background: transparent;
      color: var(--text-muted, #9aa3b2);
      cursor: pointer;
      transition: background 120ms ease, border-color 120ms ease, color 120ms ease;
    }
    .chat-msg-action:hover:not(:disabled) {
      background: var(--surface-2, rgba(255,255,255,0.06));
      border-color: var(--border, rgba(255,255,255,0.12));
      color: var(--text, #E7E9EE);
    }
    .chat-msg-action:disabled {
      opacity: 0.4;
      cursor: not-allowed;
    }
    .chat-msg-tooltip {
      position: absolute;
      bottom: calc(100% + 4px);
      right: 0;
      padding: 2px 6px;
      font-size: 10px;
      color: var(--text, #E7E9EE);
      background: var(--surface-2, #1a1d23);
      border: 1px solid var(--border, rgba(255,255,255,0.16));
      border-radius: 3px;
      white-space: nowrap;
      pointer-events: none;
      animation: chat-msg-tooltip-fade 1500ms ease forwards;
    }
    @keyframes chat-msg-tooltip-fade {
      0%, 70% { opacity: 1; }
      100% { opacity: 0; }
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
    /* Actions row: keep "Show package JSON" disclosure on the left and the download button on
       the right so the download stays visible without expanding the JSON. */
    .chat-msg-body--markdown .cf-workflow-package-actions {
      display: flex;
      align-items: flex-start;
      gap: 8px;
    }
    .chat-msg-body--markdown .cf-workflow-package-detail {
      flex: 1 1 auto;
      min-width: 0;
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
    .chat-msg-body--markdown .cf-workflow-package-download {
      flex: 0 0 auto;
      appearance: none;
      cursor: pointer;
      font-size: 11px;
      padding: 2px 8px;
      border-radius: 4px;
      border: 1px solid var(--border, rgba(255,255,255,0.16));
      background: transparent;
      color: var(--text-muted, #9aa3b2);
      transition: background 120ms ease, border-color 120ms ease, color 120ms ease;
    }
    .chat-msg-body--markdown .cf-workflow-package-download:hover {
      background: var(--surface-2, rgba(255,255,255,0.06));
      border-color: var(--border-2, rgba(255,255,255,0.24));
      color: var(--text, #E7E9EE);
    }
  `],
})
export class ChatMessageComponent {
  private readonly host = inject(ElementRef<HTMLElement>);

  readonly message = input.required<ChatMessageView>();
  readonly forkRequested = output<string>();

  protected readonly copied = signal(false);
  private copiedTimer: ReturnType<typeof setTimeout> | null = null;

  readonly renderedHtml = computed(() => renderMarkdown(this.message().content));

  protected readonly showActions = computed(() => {
    const m = this.message();
    return m.role === 'assistant' && !m.pending;
  });

  protected roleLabel(): string {
    switch (this.message().role) {
      case 'user': return 'You';
      case 'assistant': return 'Assistant';
      case 'system': return 'System';
    }
  }

  protected onCopyClick(): void {
    const content = this.message().content ?? '';
    void writeToClipboard(content).then(ok => {
      if (!ok) return;
      this.copied.set(true);
      if (this.copiedTimer !== null) {
        clearTimeout(this.copiedTimer);
      }
      this.copiedTimer = setTimeout(() => {
        this.copied.set(false);
        this.copiedTimer = null;
      }, 1500);
    });
  }

  protected onForkClick(): void {
    const id = this.message().id;
    if (!id) return;
    this.forkRequested.emit(id);
  }

  /**
   * Workflow-package render emits a button with class `.cf-workflow-package-download` whose JSON
   * lives inside the sibling `<details><pre><code>`. Angular can't bind events to innerHTML, so
   * we delegate from the host: read the JSON out of the DOM and trigger a download.
   */
  @HostListener('click', ['$event'])
  protected onHostClick(event: MouseEvent): void {
    const target = event.target as HTMLElement | null;
    const button = target?.closest('.cf-workflow-package-download') as HTMLButtonElement | null;
    if (!button || !this.host.nativeElement.contains(button)) {
      return;
    }
    event.preventDefault();
    const container = button.closest('.cf-workflow-package');
    const code = container?.querySelector('.cf-workflow-package-detail pre code') as HTMLElement | null;
    const json = code?.textContent ?? '';
    if (!json) return;
    const filename = button.dataset['cfFilename'] || 'workflow.json';
    triggerJsonDownload(json, filename);
  }
}

async function writeToClipboard(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {
    // fall through to legacy path
  }
  try {
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.setAttribute('readonly', '');
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    const ok = document.execCommand('copy');
    document.body.removeChild(ta);
    return ok;
  } catch {
    return false;
  }
}

function triggerJsonDownload(json: string, filename: string): void {
  const blob = new Blob([json], { type: 'application/json;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.style.display = 'none';
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  setTimeout(() => URL.revokeObjectURL(url), 0);
}
