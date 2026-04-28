import { ChangeDetectionStrategy, Component, ElementRef, ViewChild, computed, effect, inject, input, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { Subscription } from 'rxjs';
import { AuthService } from '../../auth/auth.service';
import { AssistantApi, AssistantMessage, AssistantScope } from '../../core/assistant.api';
import { AssistantStreamEvent, streamAssistantTurn } from '../../core/assistant-stream';
import { PageContext, pageContextToDto } from '../../core/page-context';
import { suggestionChipsFor } from '../../core/suggestion-chips';
import { ChatComposerComponent } from './chat-composer.component';
import { ChatMessageComponent, ChatMessageView } from './chat-message.component';
import { ChatToolCallComponent, ChatToolCallView } from './chat-tool-call.component';

interface PendingAssistantTurn {
  /** The assistant message-id assigned by the server once it persists; null while streaming. */
  serverId: string | null;
  /** Running content built up from text-delta events. */
  content: string;
  provider: string | null;
  model: string | null;
}

/**
 * One row in the rendered thread. `kind` discriminates between a chat message bubble and a
 * tool-call card so the template can render the right component for each entry without losing
 * insertion order — tool calls land between the user prompt and the final assistant answer.
 */
type ThreadEntry =
  | ({ kind: 'message' } & ChatMessageView)
  | ({ kind: 'tool' } & ChatToolCallView);

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
  imports: [ChatMessageComponent, ChatComposerComponent, ChatToolCallComponent],
  template: `
    <section class="chat-panel" [attr.data-scope-kind]="scope().kind">
      <header class="chat-panel-head">
        <span class="chat-panel-title">{{ titleText() }}</span>
        @if (conversationId()) {
          <span class="chat-panel-id" [title]="conversationId()!">conv {{ shortId() }}</span>
        }
      </header>

      <div class="chat-panel-thread" #threadEl role="log" aria-live="polite">
        @if (loadFailed()) {
          <p class="chat-panel-error">{{ loadFailed() }}</p>
        } @else if (thread().length === 0 && !loading()) {
          <p class="chat-panel-empty">No messages yet — say hello.</p>
        } @else {
          @for (entry of thread(); track entry.kind === 'message' ? 'm:' + (entry.id ?? entry.role + entry.content.length) : 't:' + entry.id) {
            @switch (entry.kind) {
              @case ('message') {
                <cf-chat-message [message]="entry" />
              }
              @case ('tool') {
                <cf-chat-tool-call [view]="entry" />
              }
            }
          }
        }
        @if (turnError()) {
          <p class="chat-panel-error">{{ turnError() }}</p>
        }
      </div>

      @if (chips().length > 0) {
        <div class="chat-panel-chips" data-testid="suggestion-chips">
          @for (c of chips(); track c.label) {
            <button
              type="button"
              class="chat-chip"
              [disabled]="streaming() || !conversationId() || !!loadFailed()"
              (click)="sendMessage(c.prompt)"
            >{{ c.label }}</button>
          }
        </div>
      }

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
    .chat-panel-chips {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      padding: 8px 12px 4px;
      border-top: 1px solid var(--border, rgba(255,255,255,0.06));
    }
    .chat-chip {
      appearance: none;
      background: var(--surface, #131519);
      border: 1px solid var(--border, rgba(255,255,255,0.08));
      color: var(--text, #E7E9EE);
      font-size: 11px;
      padding: 4px 10px;
      border-radius: 999px;
      cursor: pointer;
      transition: background var(--transition, 150ms ease), border-color var(--transition, 150ms ease);
    }
    .chat-chip:hover:not(:disabled) {
      background: var(--surface-2, rgba(255,255,255,0.04));
      border-color: var(--border-2, rgba(255,255,255,0.16));
    }
    .chat-chip:disabled {
      opacity: 0.5;
      cursor: not-allowed;
    }
  `],
})
export class ChatPanelComponent {
  private readonly api = inject(AssistantApi);
  private readonly auth = inject(AuthService);

  /** The conversation scope. Changing it re-resolves the conversation. */
  readonly scope = input.required<AssistantScope>();

  /**
   * HAA-8: optional page context. When supplied, the panel renders config-driven suggestion
   * chips above the composer and forwards the context with each turn so the backend can inject
   * a `<current-page-context>` system-message snippet for implicit "this trace" / "this node"
   * resolution. Mounts that don't pass this (the home page's main-pane chat) get neither chips
   * nor injection.
   */
  readonly pageContext = input<PageContext | null>(null);

  @ViewChild('threadEl') private threadRef?: ElementRef<HTMLDivElement>;

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
  /**
   * Tool-call cards for the in-flight turn, in insertion order. Reset at the start of each new
   * user message — only the FINAL assistant text is persisted to history, so tool cards from
   * prior turns can't be reconstructed on reload (which is fine: they're a debugging aid for the
   * current exchange).
   */
  private readonly toolCalls = signal<ChatToolCallView[]>([]);

  private streamSub: Subscription | null = null;

  protected readonly thread = computed<ThreadEntry[]>(() => {
    const out: ThreadEntry[] = [];
    for (const m of this.history()) {
      if (m.role === 'system') continue;
      out.push({
        kind: 'message',
        id: m.id,
        role: m.role,
        content: m.content,
        provider: m.provider,
        model: m.model,
      });
    }
    const optimistic = this.optimisticUser();
    if (optimistic !== null) {
      out.push({ kind: 'message', id: null, role: 'user', content: optimistic });
    }
    for (const tc of this.toolCalls()) {
      out.push({ kind: 'tool', ...tc });
    }
    const pending = this.pending();
    if (pending !== null) {
      out.push({
        kind: 'message',
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

  protected readonly chips = computed(() => {
    const ctx = this.pageContext();
    return ctx ? suggestionChipsFor(ctx) : [];
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
    this.toolCalls.set([]);
    this.pending.set({ serverId: null, content: '', provider: null, model: null });
    this.streaming.set(true);

    const ctx = this.pageContext();
    const dto = ctx ? pageContextToDto(ctx, window.location.pathname) : undefined;
    this.streamSub = streamAssistantTurn(conversationId, content, this.auth, dto).subscribe({
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
    this.toolCalls.set([]);
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
      case 'tool-call': {
        // New tool card in pending state. argsPreview is a single-line stringification — the user
        // can expand the card to see the full payload when the server fills in the result.
        this.toolCalls.update(list => [...list, {
          id: evt.id,
          name: evt.name,
          status: 'pending',
          argsPreview: stringifyArgs(evt.arguments),
        }]);
        this.scrollToBottom();
        break;
      }
      case 'tool-result': {
        // Pair with the matching tool-call by id and flip its status. If we get a result without
        // a prior call (shouldn't happen, but server bugs are server bugs), drop it on the floor.
        this.toolCalls.update(list => list.map(card =>
          card.id === evt.id
            ? {
                ...card,
                status: evt.isError ? 'error' : 'success',
                resultPreview: evt.isError ? undefined : truncatePreview(evt.result),
                errorMessage: evt.isError ? truncatePreview(evt.result) : undefined,
              }
            : card,
        ));
        this.scrollToBottom();
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
    this.toolCalls.set([]);
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

function stringifyArgs(args: unknown): string {
  if (args === undefined || args === null) return '';
  try {
    return truncatePreview(JSON.stringify(args, null, 2));
  } catch {
    return String(args);
  }
}

function truncatePreview(value: string): string {
  const PREVIEW_CAP = 4_000;
  if (value.length <= PREVIEW_CAP) return value;
  return value.slice(0, PREVIEW_CAP) + `\n... [truncated, original was ${value.length} chars]`;
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
