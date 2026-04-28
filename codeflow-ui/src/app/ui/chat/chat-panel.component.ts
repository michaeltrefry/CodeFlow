import { ChangeDetectionStrategy, Component, ElementRef, ViewChild, computed, effect, inject, input, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { Subscription } from 'rxjs';
import { AuthService } from '../../auth/auth.service';
import { AssistantApi, AssistantMessage, AssistantScope } from '../../core/assistant.api';
import { AssistantStreamEvent, streamAssistantTurn } from '../../core/assistant-stream';
import { ChatComposerComponent } from './chat-composer.component';
import { ChatMessageComponent, ChatMessageView } from './chat-message.component';

interface PendingAssistantTurn {
  /** The assistant message-id assigned by the server once it persists; null while streaming. */
  serverId: string | null;
  /** Running content built up from text-delta events. */
  content: string;
  provider: string | null;
  model: string | null;
}

/**
 * Embeddable assistant chat panel. Connects to the HAA-1 assistant API for the supplied
 * <c>scope</c> (homepage or entity-scoped) and streams the conversation in place. Designed to
 * live in any parent — the homepage main pane or the right-rail sidebar — without knowledge of
 * the surrounding layout.
 */
@Component({
  selector: 'cf-chat-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChatMessageComponent, ChatComposerComponent],
  template: `
    <section class="chat-panel" [attr.data-scope-kind]="scope().kind">
      <header class="chat-panel-head">
        <span class="chat-panel-title">{{ titleText() }}</span>
        @if (conversationId()) {
          <span class="chat-panel-id" [title]="conversationId()!">conv {{ shortId() }}</span>
        }
      </header>

      <div class="chat-panel-thread" #thread role="log" aria-live="polite">
        @if (loadFailed()) {
          <p class="chat-panel-error">{{ loadFailed() }}</p>
        } @else if (messages().length === 0 && !loading()) {
          <p class="chat-panel-empty">No messages yet — say hello.</p>
        } @else {
          @for (msg of messages(); track msg.id ?? msg.role + msg.content.length) {
            <cf-chat-message [message]="msg" />
          }
        }
        @if (turnError()) {
          <p class="chat-panel-error">{{ turnError() }}</p>
        }
      </div>

      <cf-chat-composer
        [busy]="streaming()"
        [disabled]="!conversationId() || !!loadFailed()"
        (send)="sendMessage($event)"
        (cancel)="cancelTurn()"
      />
    </section>
  `,
  styles: [`
    .chat-panel {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 320px;
      background: var(--bg, #0B0C0E);
      border: 1px solid var(--border, rgba(255,255,255,0.08));
      border-radius: var(--radius-md, 8px);
      overflow: hidden;
    }
    .chat-panel-head {
      display: flex;
      align-items: baseline;
      justify-content: space-between;
      padding: 8px 12px;
      border-bottom: 1px solid var(--border, rgba(255,255,255,0.08));
      background: var(--surface, #131519);
    }
    .chat-panel-title {
      font-size: var(--fs-sm, 12px);
      text-transform: uppercase;
      letter-spacing: 0.06em;
      color: var(--text-muted, #9aa3b2);
    }
    .chat-panel-id {
      font-size: 11px;
      font-family: var(--font-mono, ui-monospace, SFMono-Regular, monospace);
      color: var(--text-muted, #9aa3b2);
    }
    .chat-panel-thread {
      flex: 1 1 auto;
      overflow-y: auto;
      padding: 12px;
      display: flex;
      flex-direction: column;
      gap: 10px;
    }
    .chat-panel-empty,
    .chat-panel-error {
      margin: 0;
      padding: 12px;
      font-size: var(--fs-sm, 12px);
      color: var(--text-muted, #9aa3b2);
      text-align: center;
    }
    .chat-panel-error {
      color: var(--sem-red, #f85149);
    }
  `],
})
export class ChatPanelComponent {
  private readonly api = inject(AssistantApi);
  private readonly auth = inject(AuthService);

  /** The conversation scope. Changing it re-resolves the conversation. */
  readonly scope = input.required<AssistantScope>();

  @ViewChild('thread') private threadRef?: ElementRef<HTMLDivElement>;

  protected readonly conversationId = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly streaming = signal(false);
  protected readonly loadFailed = signal<string | null>(null);
  protected readonly turnError = signal<string | null>(null);
  /** Persisted history rows. */
  private readonly history = signal<AssistantMessage[]>([]);
  /** The in-flight assistant turn (rendered on top of history while streaming). */
  private readonly pending = signal<PendingAssistantTurn | null>(null);
  /** The current user turn rendered immediately while waiting for server-id assignment. */
  private readonly optimisticUser = signal<string | null>(null);

  private streamSub: Subscription | null = null;

  protected readonly messages = computed<ChatMessageView[]>(() => {
    const out: ChatMessageView[] = [];
    for (const m of this.history()) {
      if (m.role === 'system') continue;
      out.push({
        id: m.id,
        role: m.role,
        content: m.content,
        provider: m.provider,
        model: m.model,
      });
    }
    const optimistic = this.optimisticUser();
    if (optimistic !== null) {
      out.push({ id: null, role: 'user', content: optimistic });
    }
    const pending = this.pending();
    if (pending !== null) {
      out.push({
        id: pending.serverId,
        role: 'assistant',
        content: pending.content,
        provider: pending.provider,
        model: pending.model,
        pending: pending.serverId === null,
      });
    }
    return out;
  });

  protected readonly shortId = computed(() => {
    const id = this.conversationId();
    return id ? id.slice(0, 8) : '';
  });

  constructor() {
    // Resolve the conversation whenever the scope input changes. effect() runs once on mount
    // and again on each scope rebind.
    effect(() => {
      const scope = this.scope();
      this.resetForScope();
      this.loadConversation(scope);
    });
  }

  protected titleText(): string {
    const scope = this.scope();
    if (scope.kind === 'homepage') {
      return 'CodeFlow assistant';
    }
    return `${scope.entityType ?? 'entity'} assistant`;
  }

  protected sendMessage(content: string): void {
    const conversationId = this.conversationId();
    if (!conversationId || this.streaming()) {
      return;
    }
    this.turnError.set(null);
    this.optimisticUser.set(content);
    this.pending.set({ serverId: null, content: '', provider: null, model: null });
    this.streaming.set(true);

    this.streamSub = streamAssistantTurn(conversationId, content, this.auth).subscribe({
      next: evt => this.handleStreamEvent(evt),
      error: err => {
        this.streaming.set(false);
        this.streamSub = null;
        this.turnError.set(formatError(err));
        // Roll back: drop the in-flight assistant bubble, keep the user message visible so the
        // user can retry.
        this.pending.set(null);
      },
      complete: () => {
        this.streaming.set(false);
        this.streamSub = null;
      },
    });
  }

  protected cancelTurn(): void {
    if (this.streamSub) {
      this.streamSub.unsubscribe();
      this.streamSub = null;
    }
    this.streaming.set(false);
    this.pending.set(null);
    this.optimisticUser.set(null);
  }

  private handleStreamEvent(evt: AssistantStreamEvent): void {
    switch (evt.kind) {
      case 'user-message-persisted': {
        // Server assigned an id + sequence; absorb into history and clear the optimistic copy.
        this.history.update(h => [...h, evt.message]);
        this.optimisticUser.set(null);
        this.scrollToBottom();
        break;
      }
      case 'text-delta': {
        const cur = this.pending();
        if (cur) {
          this.pending.set({ ...cur, content: cur.content + evt.delta });
          this.scrollToBottom();
        }
        break;
      }
      case 'token-usage': {
        const cur = this.pending();
        if (cur) {
          this.pending.set({ ...cur, provider: evt.provider, model: evt.model });
        }
        break;
      }
      case 'assistant-message-persisted': {
        this.history.update(h => [...h, evt.message]);
        this.pending.set(null);
        this.scrollToBottom();
        break;
      }
      case 'error': {
        this.turnError.set(evt.message);
        this.pending.set(null);
        break;
      }
      case 'done':
        // Terminal — nothing to render. Subscription completes naturally.
        break;
    }
  }

  private resetForScope(): void {
    this.cancelTurn();
    this.conversationId.set(null);
    this.history.set([]);
    this.loadFailed.set(null);
    this.turnError.set(null);
  }

  private loadConversation(scope: AssistantScope): void {
    this.loading.set(true);
    this.api.getOrCreate(scope).subscribe({
      next: payload => {
        this.conversationId.set(payload.conversation.id);
        this.history.set(payload.messages);
        this.loading.set(false);
        queueMicrotask(() => this.scrollToBottom());
      },
      error: err => {
        this.loading.set(false);
        this.loadFailed.set(formatError(err));
      },
    });
  }

  private scrollToBottom(): void {
    queueMicrotask(() => {
      const el = this.threadRef?.nativeElement;
      if (el) {
        el.scrollTop = el.scrollHeight;
      }
    });
  }
}

function formatError(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    // HttpClient wraps non-2xx responses; the default toString() yields "[object Object]".
    const reason = typeof err.error === 'string'
      ? err.error
      : err.error?.error ?? err.message;
    return `${err.status} ${err.statusText}${reason ? ` — ${reason}` : ''}`;
  }
  if (err instanceof Error) {
    return err.message;
  }
  return String(err);
}


