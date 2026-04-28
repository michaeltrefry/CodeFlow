import { ChangeDetectionStrategy, Component, ElementRef, ViewChild, computed, effect, inject, input, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { Subscription } from 'rxjs';
import { AuthService } from '../../auth/auth.service';
import { AssistantApi, AssistantMessage, AssistantScope } from '../../core/assistant.api';
import { AssistantStreamEvent, streamAssistantTurn } from '../../core/assistant-stream';
import { PageContext, pageContextToDto } from '../../core/page-context';
import { suggestionChipsFor } from '../../core/suggestion-chips';
import { WorkflowsApi } from '../../core/workflows.api';
import { summarizeWorkflowPackage } from '../../core/workflow-package.utils';
import { TracesApi } from '../../core/traces.api';
import { CreateTraceRequest } from '../../core/models';
import { ChatComposerComponent } from './chat-composer.component';
import { ChatMessageComponent, ChatMessageView } from './chat-message.component';
import { ChatToolCallComponent, ChatToolCallView } from './chat-tool-call.component';

/** HAA-10: name of the assistant tool whose preview-ok result triggers a Save confirmation chip. */
const SAVE_WORKFLOW_PACKAGE_TOOL = 'save_workflow_package';

/** HAA-11: name of the assistant tool whose preview-ok result triggers a Run confirmation chip. */
const RUN_WORKFLOW_TOOL = 'run_workflow';

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
                <cf-chat-tool-call
                  [view]="entry"
                  (confirmConfirmation)="onConfirmToolCall($event)"
                  (cancelConfirmation)="onCancelToolCall($event)"
                />
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
  private readonly workflowsApi = inject(WorkflowsApi);
  private readonly tracesApi = inject(TracesApi);

  /**
   * HAA-10: per-tool-call cache of the structured `package` argument the LLM passed to
   * `save_workflow_package`. The chat-panel needs the FULL package to POST to
   * `/api/workflows/package/apply` on confirm — but `ChatToolCallView.argsPreview` only carries
   * a stringified preview. Stashing keyed by tool-call id keeps the structured payload alongside
   * the visible card without bloating the view model.
   */
  private readonly pendingSaves = new Map<string, unknown>();

  /**
   * HAA-11: per-tool-call cache of the run request the LLM proposed via `run_workflow`. Same
   * rationale as `pendingSaves` — the structured args don't survive into the view model, so we
   * stash them by id and POST on confirm.
   */
  private readonly pendingRuns = new Map<string, CreateTraceRequest>();

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
    this.pendingSaves.clear();
    this.pendingRuns.clear();
  }

  /**
   * Dispatches the user's chip confirmation to the right mutation. Each kind is gated by its
   * own auth policy on the server (WorkflowsWrite for save, TracesWrite for run); demo-mode
   * users never reach here because the underlying tool can't be invoked in the first place.
   */
  protected onConfirmToolCall(toolCallId: string): void {
    const card = this.toolCalls().find(c => c.id === toolCallId);
    const kind = card?.confirmation?.kind;
    if (kind === 'save_workflow_package') {
      this.applySaveConfirmation(toolCallId);
    } else if (kind === 'run_workflow') {
      this.applyRunConfirmation(toolCallId);
    }
  }

  /** Dismiss the chip locally; no server call. Cached payloads are dropped. */
  protected onCancelToolCall(toolCallId: string): void {
    this.pendingSaves.delete(toolCallId);
    this.pendingRuns.delete(toolCallId);
    this.updateConfirmation(toolCallId, c => ({ ...c, state: 'cancelled' }));
  }

  /**
   * HAA-10: POSTs the cached package to the existing /api/workflows/package/apply endpoint
   * (WorkflowsWrite under the logged-in user) and reflects the outcome on the chip.
   */
  private applySaveConfirmation(toolCallId: string): void {
    const pkg = this.pendingSaves.get(toolCallId);
    if (!pkg) {
      this.updateConfirmation(toolCallId, c => ({
        ...c,
        state: 'error',
        errorMessage: 'Package payload missing — re-open the chat thread or ask the assistant to re-emit it.',
      }));
      return;
    }
    this.updateConfirmation(toolCallId, c => ({ ...c, state: 'applying' }));
    this.workflowsApi.applyPackageImport(pkg).subscribe({
      next: result => {
        this.pendingSaves.delete(toolCallId);
        this.updateConfirmation(toolCallId, c => ({
          ...c,
          state: 'success',
          applied: { kind: 'workflow', key: result.entryPoint.key, version: result.entryPoint.version },
        }));
      },
      error: err => {
        this.updateConfirmation(toolCallId, c => ({
          ...c,
          state: 'error',
          errorMessage: formatError(err),
        }));
      },
    });
  }

  /**
   * HAA-11: POSTs the cached run request to /api/traces (TracesWrite under the logged-in user)
   * and surfaces the resulting trace id on the chip with a link into the trace inspector.
   */
  private applyRunConfirmation(toolCallId: string): void {
    const req = this.pendingRuns.get(toolCallId);
    if (!req) {
      this.updateConfirmation(toolCallId, c => ({
        ...c,
        state: 'error',
        errorMessage: 'Run request payload missing — ask the assistant to propose the run again.',
      }));
      return;
    }
    this.updateConfirmation(toolCallId, c => ({ ...c, state: 'applying' }));
    this.tracesApi.create(req).subscribe({
      next: result => {
        this.pendingRuns.delete(toolCallId);
        this.updateConfirmation(toolCallId, c => ({
          ...c,
          state: 'success',
          applied: { kind: 'trace', traceId: result.traceId },
        }));
      },
      error: err => {
        this.updateConfirmation(toolCallId, c => ({
          ...c,
          state: 'error',
          errorMessage: formatError(err),
        }));
      },
    });
  }

  private updateConfirmation(
    toolCallId: string,
    next: (current: NonNullable<ChatToolCallView['confirmation']>) => NonNullable<ChatToolCallView['confirmation']>,
  ): void {
    this.toolCalls.update(list => list.map(card => {
      if (card.id !== toolCallId || !card.confirmation) {
        return card;
      }
      return { ...card, confirmation: next(card.confirmation) };
    }));
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
        // HAA-10/HAA-11: stash structured args for mutating tools so we can POST them to the
        // matching mutation endpoint when the user confirms via the chip.
        if (evt.arguments && typeof evt.arguments === 'object') {
          if (evt.name === SAVE_WORKFLOW_PACKAGE_TOOL) {
            const pkg = (evt.arguments as Record<string, unknown>)['package'];
            if (pkg) {
              this.pendingSaves.set(evt.id, pkg);
            }
          } else if (evt.name === RUN_WORKFLOW_TOOL) {
            const req = toCreateTraceRequest(evt.arguments as Record<string, unknown>);
            if (req) {
              this.pendingRuns.set(evt.id, req);
            }
          }
        }
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
        let confirmation: ChatToolCallView['confirmation'] | undefined;
        if (!evt.isError) {
          if (evt.name === SAVE_WORKFLOW_PACKAGE_TOOL) {
            confirmation = buildSaveConfirmationView(evt.result, this.pendingSaves.get(evt.id));
          } else if (evt.name === RUN_WORKFLOW_TOOL) {
            confirmation = buildRunConfirmationView(evt.result, this.pendingRuns.get(evt.id));
          }
        }
        this.toolCalls.update(list => list.map(card =>
          card.id === evt.id
            ? {
                ...card,
                status: evt.isError ? 'error' : 'success',
                resultPreview: evt.isError ? undefined : truncatePreview(evt.result),
                errorMessage: evt.isError ? truncatePreview(evt.result) : undefined,
                confirmation,
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

/**
 * HAA-10: when the assistant's `save_workflow_package` tool returns a `preview_ok` verdict, the
 * chat-panel attaches a confirmation chip to the tool card so the user can authorize the save.
 * The cached package payload is held separately on the panel; this helper just produces the
 * view-model the chip renders.
 */
function buildSaveConfirmationView(
  resultJson: string,
  pkg: unknown,
): ChatToolCallView['confirmation'] | undefined {
  let parsed: { status?: unknown; entryPoint?: { key?: unknown; version?: unknown } } | null = null;
  try {
    parsed = JSON.parse(resultJson);
  } catch {
    return undefined;
  }

  if (!parsed || parsed.status !== 'preview_ok' || !pkg) {
    return undefined;
  }

  const summary = summarizeWorkflowPackage(pkg);
  const entryKey =
    typeof parsed.entryPoint?.key === 'string' ? parsed.entryPoint.key : summary?.entryPointKey ?? '';
  const entryVersion =
    typeof parsed.entryPoint?.version === 'number' ? parsed.entryPoint.version : summary?.entryPointVersion ?? 0;

  const label = entryKey
    ? `Save ${summary?.workflowName ?? entryKey} (${entryKey} v${entryVersion}) to the library?`
    : 'Save this workflow package to the library?';

  return {
    kind: 'save_workflow_package',
    prompt: label,
    confirmLabel: 'Save',
    cancelLabel: 'Cancel',
    state: 'idle',
  };
}

/**
 * HAA-11: when the assistant's `run_workflow` tool returns `preview_ok`, build the chip
 * view-model. The chip prompt names the workflow + version + supplied input keys so the user
 * sees what they're authorizing before clicking Run. The actual trace creation happens when the
 * user confirms (POST /api/traces); this helper just produces the chip view.
 */
function buildRunConfirmationView(
  resultJson: string,
  request: CreateTraceRequest | undefined,
): ChatToolCallView['confirmation'] | undefined {
  let parsed: {
    status?: unknown;
    workflow?: { key?: unknown; version?: unknown; name?: unknown };
    resolvedInputs?: Record<string, unknown>;
  } | null = null;
  try {
    parsed = JSON.parse(resultJson);
  } catch {
    return undefined;
  }

  if (!parsed || parsed.status !== 'preview_ok' || !request) {
    return undefined;
  }

  const name = typeof parsed.workflow?.name === 'string' ? parsed.workflow.name : request.workflowKey;
  const version = typeof parsed.workflow?.version === 'number' ? parsed.workflow.version : null;
  const inputCount = parsed.resolvedInputs ? Object.keys(parsed.resolvedInputs).length : 0;

  const versionPart = version !== null ? ` v${version}` : '';
  const inputsPart = inputCount > 0 ? ` with ${inputCount} input${inputCount === 1 ? '' : 's'}` : '';
  const prompt = `Run ${name}${versionPart}${inputsPart}?`;

  return {
    kind: 'run_workflow',
    prompt,
    confirmLabel: 'Run',
    cancelLabel: 'Cancel',
    state: 'idle',
  };
}

/**
 * HAA-11: parse a `run_workflow` tool-call's structured arguments into the shape
 * `TracesApi.create()` expects. Returns null if `workflowKey` / `input` aren't present (the
 * tool itself would have errored on those, but we defensively skip stashing here).
 */
function toCreateTraceRequest(args: Record<string, unknown>): CreateTraceRequest | null {
  const workflowKey = typeof args['workflowKey'] === 'string' ? (args['workflowKey'] as string) : '';
  const input = typeof args['input'] === 'string' ? (args['input'] as string) : '';
  if (!workflowKey || !input) {
    return null;
  }

  const req: CreateTraceRequest = { workflowKey, input };
  if (typeof args['workflowVersion'] === 'number') {
    req.workflowVersion = args['workflowVersion'] as number;
  }
  if (typeof args['inputFileName'] === 'string') {
    req.inputFileName = args['inputFileName'] as string;
  }
  if (args['inputs'] && typeof args['inputs'] === 'object') {
    req.inputs = args['inputs'] as Record<string, unknown>;
  }
  return req;
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
