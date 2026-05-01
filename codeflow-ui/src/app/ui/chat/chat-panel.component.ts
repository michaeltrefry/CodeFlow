import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, OnDestroy, ViewChild, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AuthService } from '../../auth/auth.service';
import {
  AssistantApi,
  AssistantDefaultsResponse,
  AssistantMessage,
  AssistantScope,
  ConversationResponse,
} from '../../core/assistant.api';
import { AssistantPreferencesService } from '../../core/assistant-preferences.service';
import { AssistantPreflightRefusal, AssistantStreamEvent, AssistantWorkspaceTargetDto, streamAssistantTurn } from '../../core/assistant-stream';
import { formatHttpError } from '../../core/format-error';
import { PageContext, pageContextToDto } from '../../core/page-context';
import { suggestionChipsFor } from '../../core/suggestion-chips';
import { WorkflowPackageImportPreview, WorkflowsApi } from '../../core/workflows.api';
import { summarizeWorkflowPackage } from '../../core/workflow-package.utils';
import { TracesApi } from '../../core/traces.api';
import { CreateTraceRequest, LlmProviderKey, LlmProviderModelOption, ReplayRequest } from '../../core/models';
import { IconComponent } from '../icon.component';
import { ChatComposerComponent } from './chat-composer.component';
import { ChatMessageComponent, ChatMessageView } from './chat-message.component';
import { ChatToolCallComponent, ChatToolCallView } from './chat-tool-call.component';
import { ChatToolbarComponent } from './chat-toolbar.component';
import { extractWorkflowPackagesFromMarkdown } from './markdown';

/** HAA-10: name of the assistant tool whose preview-ok result triggers a Save confirmation chip. */
const SAVE_WORKFLOW_PACKAGE_TOOL = 'save_workflow_package';

/** HAA-11: name of the assistant tool whose preview-ok result triggers a Run confirmation chip. */
const RUN_WORKFLOW_TOOL = 'run_workflow';

/** HAA-13: name of the assistant tool whose preview-ok result triggers a Replay confirmation chip. */
const PROPOSE_REPLAY_WITH_EDIT_TOOL = 'propose_replay_with_edit';

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

interface PinnedMutationChip {
  id: string;
  state: NonNullable<ChatToolCallView['confirmation']>['state'];
  title: string;
  message: string | null;
  confirmLabel: string;
  cancelLabel: string;
  canConfirm: boolean;
  canCancel: boolean;
  linkUrl: string | null;
  linkLabel: string | null;
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
  imports: [
    ChatMessageComponent,
    ChatComposerComponent,
    ChatToolCallComponent,
    ChatToolbarComponent,
    IconComponent,
  ],
  template: `
    <section class="chat-panel" [attr.data-scope-kind]="effectiveScope().kind">
      <header class="chat-panel-head">
        <span class="chat-panel-title">{{ titleText() }}</span>
        <div class="chat-panel-actions">
          @if (conversationId()) {
            <span class="chat-panel-id" [title]="conversationId()!">conv {{ shortId() }}</span>
          }
          <button
            type="button"
            class="chat-panel-new"
            title="Start new conversation"
            aria-label="Start new conversation"
            [disabled]="loading() || streaming()"
            (click)="startNewConversation()"
          >
            <cf-icon name="plus"></cf-icon>
          </button>
        </div>
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
                <cf-chat-message
                  [message]="entry"
                  (forkRequested)="onForkRequested($event)"
                />
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
        @if (preflightError(); as pf) {
          <div class="chat-panel-preflight" data-testid="preflight-banner" role="status">
            <div class="preflight-head">
              <span class="preflight-chip">preflight</span>
              <span class="preflight-metric">
                clarity {{ (pf.overallScore * 100).toFixed(0) }}% · needs {{ (pf.threshold * 100).toFixed(0) }}%
              </span>
            </div>
            @if (preflightWeakest(); as weakest) {
              <p class="preflight-reason">{{ weakest.reason }}</p>
            }
            @if (pf.clarificationQuestions.length > 0) {
              <ul class="preflight-questions">
                @for (q of pf.clarificationQuestions; track q) {
                  <li>{{ q }}</li>
                }
              </ul>
            }
          </div>
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

      @if (workspacePrompt(); as ws) {
        <div class="ws-prompt" data-testid="workspace-switch-prompt">
          <div class="ws-prompt-title">Use this trace’s workspace?</div>
          <div class="ws-prompt-hint">
            Host tools (<span class="mono">read_file</span>, <span class="mono">run_command</span>)
            will operate on the trace’s files instead of your conversation’s.
          </div>
          <div class="ws-prompt-actions">
            <button
              type="button"
              class="ws-prompt-btn ghost"
              [disabled]="streaming()"
              (click)="onDeclineWorkspaceSwitch('workspace:' + ws.traceId)"
            >Keep mine</button>
            <button
              type="button"
              class="ws-prompt-btn primary"
              [disabled]="streaming()"
              (click)="onAcceptWorkspaceSwitch('workspace:' + ws.traceId)"
            >Switch</button>
          </div>
        </div>
      }

      @for (chip of pendingMutationChips(); track chip.id) {
        <div
          class="ws-prompt mutation-pin"
          [attr.data-state]="chip.state"
          data-testid="pending-mutation-chip"
        >
          <div class="ws-prompt-title">{{ chip.title }}</div>
          @if (chip.message) {
            <div class="ws-prompt-hint">{{ chip.message }}</div>
          }
          <div class="ws-prompt-actions">
            @if (chip.linkUrl) {
              <a class="ws-prompt-btn primary" [href]="chip.linkUrl">{{ chip.linkLabel }}</a>
            }
            @if (chip.canCancel) {
              <button
                type="button"
                class="ws-prompt-btn ghost"
                [disabled]="streaming()"
                (click)="onCancelToolCall(chip.id)"
              >{{ chip.cancelLabel }}</button>
            }
            @if (chip.canConfirm) {
              <button
                type="button"
                class="ws-prompt-btn primary"
                [disabled]="streaming()"
                (click)="onConfirmToolCall(chip.id)"
              >{{ chip.confirmLabel }}</button>
            }
          </div>
        </div>
      }

      <cf-chat-composer
        [busy]="streaming()"
        [disabled]="!conversationId() || !!loadFailed()"
        (send)="sendMessage($event)"
        (cancel)="cancelTurn()"
      >
        @if (layout() === 'wide') {
          <cf-chat-toolbar
            class="composer-inline-selectors"
            displayMode="selectors-only"
            chromeless
            [models]="availableModels()"
            [provider]="selectedProvider()"
            [model]="selectedModel()"
            [disabled]="streaming() || loading() || !conversationId()"
            (selectionChanged)="onSelectionChanged($event)"
          />
        }
        <cf-chat-toolbar
          class="composer-inline-tokens"
          displayMode="tokens-only"
          chromeless
          [inputTokens]="conversationInputTokens()"
          [outputTokens]="conversationOutputTokens()"
          [cap]="conversationCap()"
        />
      </cf-chat-composer>

      @if (layout() === 'compact') {
        <cf-chat-toolbar
          displayMode="selectors-only"
          [models]="availableModels()"
          [provider]="selectedProvider()"
          [model]="selectedModel()"
          [disabled]="streaming() || loading() || !conversationId()"
          (selectionChanged)="onSelectionChanged($event)"
        />
      }
    </section>
  `,
  styles: [`
    :host {
      display: flex;
      flex-direction: column;
      min-height: 0;
      height: 100%;
    }
    .chat-panel {
      display: flex;
      flex-direction: column;
      flex: 1 1 auto;
      height: 100%;
      min-height: 0;
      background: var(--bg, #0B0C0E);
      border: 1px solid var(--border, rgba(255,255,255,0.08));
      border-radius: var(--radius-md, 8px);
      overflow: hidden;
    }
    .chat-panel-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      flex: 0 0 auto;
      padding: 8px var(--chat-panel-head-padding-right, 12px) 8px 12px;
      border-bottom: 1px solid var(--border, rgba(255,255,255,0.08));
      background: var(--surface, #131519);
      gap: 10px;
    }
    .chat-panel-title {
      font-size: var(--fs-sm, 12px);
      text-transform: uppercase;
      letter-spacing: 0.06em;
      color: var(--text-muted, #9aa3b2);
    }
    .chat-panel-actions {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .chat-panel-id {
      font-size: 11px;
      font-family: var(--font-mono, ui-monospace, SFMono-Regular, monospace);
      color: var(--text-muted, #9aa3b2);
    }
    .chat-panel-new {
      appearance: none;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 24px;
      height: 24px;
      padding: 0;
      border: 1px solid var(--border, rgba(255,255,255,0.08));
      border-radius: 4px;
      background: transparent;
      color: var(--text-muted, #9aa3b2);
      cursor: pointer;
      transition: background 120ms ease, border-color 120ms ease, color 120ms ease;
    }
    .chat-panel-new:hover:not(:disabled) {
      background: var(--surface-2, rgba(255,255,255,0.04));
      border-color: var(--border-2, rgba(255,255,255,0.16));
      color: var(--text, #E7E9EE);
    }
    .chat-panel-new:disabled {
      opacity: 0.45;
      cursor: not-allowed;
    }
    .chat-panel-thread {
      flex: 1 1 auto;
      min-height: 0;
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
    /* sc-274 phase 2 — preflight clarification banner. Warn-tinted (not error-tinted) because
       this is "you didn't say enough yet" feedback, not a failure. Mirrors the replay panel's
       preflight banner so the two surfaces feel consistent. */
    .chat-panel-preflight {
      flex: 0 0 auto;
      display: flex;
      flex-direction: column;
      gap: 6px;
      margin: 4px 0 0;
      padding: 10px 12px;
      border: 1px solid var(--sem-amber, #d29922);
      border-radius: var(--radius-md, 8px);
      background: color-mix(in oklab, var(--sem-amber, #d29922) 10%, var(--surface, #131519));
    }
    .chat-panel-preflight .preflight-head {
      display: flex;
      align-items: baseline;
      gap: 8px;
      flex-wrap: wrap;
    }
    .chat-panel-preflight .preflight-chip {
      display: inline-block;
      padding: 1px 6px;
      font-size: 10px;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      color: var(--sem-amber, #d29922);
      border: 1px solid var(--sem-amber, #d29922);
      border-radius: 3px;
      line-height: 1.3;
    }
    .chat-panel-preflight .preflight-metric {
      font-size: 11px;
      color: var(--text-muted, #9aa3b2);
      font-family: var(--font-mono, ui-monospace, SFMono-Regular, monospace);
    }
    .chat-panel-preflight .preflight-reason {
      margin: 0;
      font-size: var(--fs-sm, 12px);
      color: var(--text-muted, #9aa3b2);
      line-height: 1.4;
      overflow-wrap: anywhere;
    }
    .chat-panel-preflight .preflight-questions {
      margin: 0;
      padding-left: 18px;
      font-size: var(--fs-md, 13px);
      color: var(--text, #E7E9EE);
      line-height: 1.4;
      display: flex;
      flex-direction: column;
      gap: 4px;
    }
    .chat-panel-preflight .preflight-questions li {
      overflow-wrap: anywhere;
    }
    .chat-panel-chips {
      display: flex;
      flex-wrap: wrap;
      flex: 0 0 auto;
      gap: 6px;
      padding: 8px 12px 4px;
      border-top: 1px solid var(--border, rgba(255,255,255,0.06));
    }
    .ws-prompt {
      flex: 0 0 auto;
      display: flex;
      flex-direction: column;
      gap: 6px;
      margin: 8px 12px 0;
      padding: 10px 12px;
      border-top: none;
      border: 1px solid var(--accent, #5765ff);
      border-radius: var(--radius-md, 8px);
      background: color-mix(in oklab, var(--accent, #5765ff) 8%, var(--surface, #131519));
      box-sizing: border-box;
      max-width: calc(100% - 24px);
      overflow: hidden;
      animation: workspace-prompt-slide-up 180ms ease-out;
    }
    @keyframes workspace-prompt-slide-up {
      from { transform: translateY(8px); opacity: 0; }
      to   { transform: translateY(0);    opacity: 1; }
    }
    .ws-prompt-title {
      font-size: var(--fs-md, 13px);
      font-weight: 600;
      color: var(--text, #E7E9EE);
      line-height: 1.3;
      overflow-wrap: anywhere;
    }
    .ws-prompt-hint {
      font-size: var(--fs-sm, 12px);
      color: var(--text-muted, #9aa3b2);
      line-height: 1.4;
      overflow-wrap: anywhere;
    }
    .ws-prompt-hint .mono {
      font-family: var(--font-mono, ui-monospace, SFMono-Regular, monospace);
      font-size: 11px;
      color: var(--text, #E7E9EE);
    }
    .ws-prompt-actions {
      display: flex;
      gap: 6px;
      justify-content: flex-end;
      margin-top: 2px;
      flex-wrap: wrap;
    }
    .ws-prompt-btn {
      appearance: none;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      cursor: pointer;
      font-size: 11px;
      padding: 4px 10px;
      border-radius: 4px;
      border: 1px solid var(--border, rgba(255,255,255,0.16));
      background: transparent;
      color: var(--text, #E7E9EE);
      text-decoration: none;
      transition: background 120ms ease, border-color 120ms ease;
    }
    .ws-prompt-btn:hover:not(:disabled) {
      background: var(--surface-2, rgba(255,255,255,0.06));
    }
    .ws-prompt-btn.primary {
      background: var(--accent, #5765ff);
      border-color: var(--accent, #5765ff);
      color: #fff;
    }
    .ws-prompt-btn.primary:hover:not(:disabled) {
      filter: brightness(1.1);
    }
    .ws-prompt-btn:disabled {
      opacity: 0.5;
      cursor: not-allowed;
    }
    cf-chat-composer {
      flex: 0 0 auto;
    }
    .composer-inline-selectors {
      flex: 1 1 auto;
      min-width: 0;
      display: block;
    }
    .composer-inline-tokens {
      flex: 0 0 auto;
      margin-left: auto;
      display: block;
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
export class ChatPanelComponent implements OnDestroy {
  private readonly api = inject(AssistantApi);
  private readonly auth = inject(AuthService);
  private readonly workflowsApi = inject(WorkflowsApi);
  private readonly tracesApi = inject(TracesApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly preferences = inject(AssistantPreferencesService);
  private readonly destroyRef = inject(DestroyRef);

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

  /**
   * HAA-13: per-tool-call cache of the replay-with-edit request the LLM proposed via
   * `propose_replay_with_edit`. Carries both the original trace id (for the URL) and the
   * `ReplayRequest` body the chat-panel POSTs to /api/traces/{id}/replay on confirm.
   */
  private readonly pendingReplays = new Map<string, { originalTraceId: string; request: ReplayRequest }>();

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

  /** Optional explicit conversation to load instead of the latest conversation for the scope. */
  readonly conversationIdOverride = input<string | null>(null);

  /**
   * Layout flavor. 'wide' inlines the provider/model selectors and the live token chip into the
   * composer's send-button row (used by the homepage main pane where horizontal space is plenty).
   * 'compact' (the default) keeps the selectors in a dedicated strip below the composer and only
   * inlines the token chip — fits the right-rail sidebar width without wrapping.
   */
  readonly layout = input<'wide' | 'compact'>('compact');

  @ViewChild('threadEl') private threadRef?: ElementRef<HTMLDivElement>;

  protected readonly conversationId = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly streaming = signal(false);
  protected readonly loadFailed = signal<string | null>(null);
  protected readonly turnError = signal<string | null>(null);
  /**
   * sc-274 phase 2 — populated when the server returns a 422 preflight refusal before opening
   * the SSE stream. Drives the preflight banner (clarification questions + score/threshold +
   * lowest-dimension reason). Cleared on the next send attempt and on cancel — mutually
   * exclusive with {@link turnError} (the stream-error path stays for non-preflight failures).
   */
  protected readonly preflightError = signal<AssistantPreflightRefusal | null>(null);

  /** sc-274 phase 2 — convenience for the template: the lowest-scoring dimension with a reason. */
  protected readonly preflightWeakest = computed(() => {
    const refusal = this.preflightError();
    if (!refusal) return null;
    let weakest: AssistantPreflightRefusal['dimensions'][number] | null = null;
    for (const dim of refusal.dimensions) {
      if (!weakest || dim.score < weakest.score) {
        weakest = dim;
      }
    }
    return weakest && weakest.reason ? weakest : null;
  });
  /**
   * The scope returned by the server for the conversation that's actually loaded. Drives the
   * panel's header text + scope attribute so an entity-scoped thread surfaced in the homepage
   * sidebar (via `?assistantConversation=<id>`) titles itself correctly instead of inheriting
   * the sidebar's mount-time `homepage` scope.
   */
  private readonly loadedScope = signal<AssistantScope | null>(null);
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

  /** HAA-15/16 — server defaults snapshot. Loaded once per mount; refreshed on demand. */
  private readonly defaults = signal<AssistantDefaultsResponse | null>(null);

  /** HAA-17 — cumulative input tokens for the loaded conversation. Updated live during a turn. */
  protected readonly conversationInputTokens = signal(0);

  /** HAA-17 — cumulative output tokens; mirrors {@link conversationInputTokens}. */
  protected readonly conversationOutputTokens = signal(0);

  /** HAA-15 — admin-configured per-conversation cap from {@link AssistantSettingsResponse}. */
  protected readonly conversationCap = signal<number | null>(null);

  /** HAA-16 — currently-selected provider override (null = use server default). */
  protected readonly selectedProvider = signal<LlmProviderKey | null>(null);

  /** HAA-16 — currently-selected model override (null = use server default for provider). */
  protected readonly selectedModel = signal<string | null>(null);

  /** HAA-16 — flat list of (provider, model) pairs the operator has configured. */
  protected readonly availableModels = computed<LlmProviderModelOption[]>(
    () => this.defaults()?.models ?? [],
  );

  /**
   * HAA-20 — Tool-call confirmation chips that are still awaiting the user's click. Surfaced as
   * a pinned strip above the composer so they don't scroll off-screen when the assistant
   * continues with a long explanation after the tool result. Once the user clicks Confirm /
   * Cancel, the chip transitions out of `idle`, drops out of this list, and the inline
   * resolved-state banner inside the tool-call card carries the result.
   */
  protected readonly pendingMutationChips = computed(() =>
    this.toolCalls()
      .map(tc => buildPinnedMutationChip(tc))
      .filter((chip): chip is PinnedMutationChip => chip !== null),
  );

  /**
   * HAA-19 — current workspace selection for this conversation. Defaults to 'conversation' (the
   * assistant's own per-chat dir). Set to a trace id by user confirmation when navigating to a
   * trace page; auto-reverts to 'conversation' when navigating away. The next outgoing turn
   * carries the selection as a `workspaceOverride`.
   */
  private readonly activeWorkspace = signal<{ kind: 'conversation' } | { kind: 'trace'; traceId: string }>(
    { kind: 'conversation' },
  );

  /**
   * HAA-19 — pending confirmation prompt when the user lands on a trace page and we haven't yet
   * asked / they haven't decided for that trace this visit. Resolves to null on Yes/No or on
   * navigation away from the trace.
   */
  protected readonly workspacePrompt = signal<{ traceId: string } | null>(null);

  /**
   * HAA-19 — set of trace ids the user has explicitly declined for the current navigation cycle.
   * Cleared on every page change so re-visiting a previously-declined trace re-prompts. Tracking
   * acceptance state in {@link activeWorkspace} instead would mean "yes once, yes forever";
   * separating decline state lets the user revisit the choice on each new trace visit.
   */
  private readonly declinedTraceIds = new Set<string>();


  private streamSub: Subscription | null = null;

  protected readonly thread = computed<ThreadEntry[]>(() => {
    // Tool calls always belong to the LATEST assistant turn (toolCalls is reset on every new
    // user submission). Render them BEFORE the assistant's reply so the temporal flow reads
    // user → tool calls → assistant text, matching the order things actually happened.
    //
    // Where "the assistant's reply" is depends on stream state:
    //   - In-flight (pending != null): the streaming text bubble at the end. Tool cards go
    //     between the (optimistic) user message and the pending text.
    //   - Just-persisted (pending == null): the most recent assistant message in history. Tool
    //     cards get spliced in just before it.
    const out: ThreadEntry[] = [];
    const messages = this.history().filter(m => m.role !== 'system');
    const tools = this.toolCalls();
    const pending = this.pending();
    const optimistic = this.optimisticUser();

    // Find the last persisted assistant message — only relevant when there's no streaming
    // bubble, since otherwise the streaming bubble is the destination.
    let toolInsertIdx = -1;
    if (pending === null && tools.length > 0) {
      for (let i = messages.length - 1; i >= 0; i--) {
        if (messages[i].role === 'assistant') { toolInsertIdx = i; break; }
      }
    }

    for (let i = 0; i < messages.length; i++) {
      if (i === toolInsertIdx) {
        for (const tc of tools) out.push({ kind: 'tool', ...tc });
      }
      const m = messages[i];
      out.push({
        kind: 'message',
        id: m.id,
        role: m.role,
        content: m.content,
        provider: m.provider,
        model: m.model,
      });
    }

    if (optimistic !== null) {
      out.push({ kind: 'message', id: null, role: 'user', content: optimistic });
    }

    // In-flight: tool cards render between the user message and the streaming reply text.
    if (pending !== null) {
      for (const tc of tools) out.push({ kind: 'tool', ...tc });
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

  /** Loaded scope (from the server payload) takes precedence so the title reflects the actual
   *  conversation rather than the mount-time `scope` input. */
  protected readonly effectiveScope = computed<AssistantScope>(() => this.loadedScope() ?? this.scope());

  protected readonly chips = computed(() => {
    const ctx = this.pageContext();
    return ctx ? suggestionChipsFor(ctx) : [];
  });

  constructor() {
    // Resolve the conversation whenever the scope or override input changes. The body runs
    // through `untracked` so reads/writes against panel state don't re-arm the effect — only
    // the input reads above are tracked. Skips the load when the override already matches the
    // currently-loaded conversation (e.g. just-created threads where startNewConversation
    // already populated state and is now reflecting the id back into the URL).
    effect(() => {
      const scope = this.scope();
      const override = this.conversationIdOverride();
      untracked(() => {
        if (override && override === this.conversationId()) {
          return;
        }
        this.resetForScope();
        if (override) {
          this.loadConversationById(override);
        } else {
          this.loadConversation(scope);
        }
      });
    });

    // HAA-19 — observe the page context to drive the workspace prompt + auto-revert. On entry to
    // a trace page that we haven't already accepted/declined, surface a confirmation chip. On
    // leaving a trace page, snap the active workspace back to 'conversation' so the next turn
    // carries the conversation override (and the chat service emits a switch-back notice).
    effect(() => {
      const ctx = this.pageContext();
      untracked(() => {
        if (ctx?.kind === 'trace' && ctx.traceId) {
          const traceId = ctx.traceId;
          const active = this.activeWorkspace();
          if (active.kind === 'trace' && active.traceId === traceId) {
            // Already on this trace's workspace; nothing to prompt.
            this.workspacePrompt.set(null);
            return;
          }
          if (this.declinedTraceIds.has(traceId)) {
            // User declined for this trace this navigation cycle; don't badger them.
            this.workspacePrompt.set(null);
            return;
          }
          this.workspacePrompt.set({ traceId });
        } else {
          // Off the trace page: clear any pending prompt + revert to conversation workspace so
          // the next turn signals the switch-back to the backend.
          this.workspacePrompt.set(null);
          this.declinedTraceIds.clear();
          if (this.activeWorkspace().kind !== 'conversation') {
            this.activeWorkspace.set({ kind: 'conversation' });
          }
        }
      });
    });

    // HAA-15/16 — load the admin-configured defaults + available models once per mount and seed
    // the cap. Selection preference is layered on top so a user's stored choice survives reloads
    // and only falls back to server defaults when nothing is stored.
    this.api.getDefaults().pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: defaults => {
        this.defaults.set(defaults);
        this.conversationCap.set(
          defaults.maxTokensPerConversation && defaults.maxTokensPerConversation > 0
            ? defaults.maxTokensPerConversation
            : null,
        );
        this.applySelectionFromStorage();
      },
      // Defaults loading is best-effort — the composer still works (selectors stay disabled
      // until configuration appears). Don't surface a banner: the assistant turn itself will
      // surface a clear error if the provider isn't configured.
      error: () => undefined,
    });
  }

  protected onSelectionChanged(selection: { provider: LlmProviderKey | null; model: string | null }): void {
    this.selectedProvider.set(selection.provider);
    this.selectedModel.set(selection.model);
    this.preferences.save(selection);
  }

  /** HAA-19 — user accepted the workspace switch for the trace currently in pageContext. */
  protected onAcceptWorkspaceSwitch(id: string): void {
    const traceId = id.startsWith('workspace:') ? id.slice('workspace:'.length) : id;
    if (!traceId) {
      return;
    }
    this.activeWorkspace.set({ kind: 'trace', traceId });
    this.workspacePrompt.set(null);
  }

  /** HAA-19 — user declined; stash the trace id so we don't re-prompt while they remain on it. */
  protected onDeclineWorkspaceSwitch(id: string): void {
    const traceId = id.startsWith('workspace:') ? id.slice('workspace:'.length) : id;
    if (traceId) {
      this.declinedTraceIds.add(traceId);
    }
    this.workspacePrompt.set(null);
  }

  /** HAA-19 — translate the active workspace into the wire DTO for the next turn. */
  private buildWorkspaceOverride(): AssistantWorkspaceTargetDto | undefined {
    const active = this.activeWorkspace();
    if (active.kind === 'trace') {
      return { kind: 'Trace', traceId: active.traceId };
    }
    return { kind: 'Conversation' };
  }

  /**
   * Read the user's stored selection (HAA-16). If the stored selection isn't valid against the
   * current models list (e.g. the operator removed a model), fall back to the server defaults so
   * the composer never shows a stale dropdown value.
   */
  private applySelectionFromStorage(): void {
    const stored = this.preferences.current();
    const list = this.defaults()?.models ?? [];
    const validProvider = stored.provider && list.some(m => m.provider === stored.provider)
      ? stored.provider
      : null;
    const validModel = stored.model && list.some(m => m.model === stored.model && (!validProvider || m.provider === validProvider))
      ? stored.model
      : null;

    if (validProvider) {
      this.selectedProvider.set(validProvider);
      this.selectedModel.set(validModel ?? null);
      return;
    }

    // No valid stored choice — clear the selection so the toolbar shows "— default —" and the
    // backend uses the admin-configured default for the next turn.
    this.selectedProvider.set(null);
    this.selectedModel.set(null);
  }

  protected titleText(): string {
    const scope = this.effectiveScope();
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
    // sc-274 phase 2 — clear any prior preflight refusal so the banner doesn't linger over a
    // new (and presumably refined) attempt.
    this.preflightError.set(null);
    this.optimisticUser.set(content);
    this.toolCalls.set([]);
    this.pending.set({ serverId: null, content: '', provider: null, model: null });
    this.streaming.set(true);

    const ctx = this.pageContext();
    const dto = ctx ? pageContextToDto(ctx, window.location.pathname) : undefined;
    const provider = this.selectedProvider() ?? undefined;
    const model = this.selectedModel() ?? undefined;
    const workspaceOverride = this.buildWorkspaceOverride();
    this.streamSub = streamAssistantTurn(conversationId, content, this.auth, {
      pageContext: dto,
      provider,
      model,
      workspaceOverride,
    }).pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: evt => this.handleStreamEvent(evt),
      error: err => {
        this.streaming.set(false);
        this.streamSub = null;
        this.turnError.set(formatHttpError(err));
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
    this.pendingReplays.clear();
    // sc-274 phase 2 — cancel also clears preflight; the user may have hit cancel because the
    // banner surfaced a needed clarification and they want to start over.
    this.preflightError.set(null);
  }

  /**
   * Forks the current conversation at the message the user clicked. Mirrors
   * {@link startNewConversation}: cancel any in-flight turn, swap in the forked thread, and
   * reflect the new id in the URL. The forked thread starts on a fresh server-assigned synthetic
   * trace id, so token totals begin at zero — surface them as such.
   */
  protected onForkRequested(messageId: string): void {
    const currentId = this.conversationId();
    if (!currentId || this.loading() || this.streaming()) {
      return;
    }
    const previousId = currentId;
    this.cancelTurn();
    this.loading.set(true);
    this.loadFailed.set(null);
    this.turnError.set(null);
    this.preflightError.set(null);
    this.history.set([]);
    this.conversationId.set(null);
    this.loadedScope.set(null);
    this.conversationInputTokens.set(0);
    this.conversationOutputTokens.set(0);

    this.api.fork(currentId, messageId).pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: payload => {
        this.applyConversationPayload(payload);
        this.syncConversationOverrideInUrl(payload.conversation.id);
      },
      error: err => {
        this.loading.set(false);
        this.conversationId.set(previousId);
        this.loadFailed.set(formatHttpError(err));
      },
    });
  }

  protected startNewConversation(): void {
    if (this.loading() || this.streaming()) {
      return;
    }

    // Use the loaded conversation's scope when available — for entity-scoped threads opened via
    // `?assistantConversation=<id>` from a homepage-mounted sidebar, the input scope is wrong.
    // Falls back to the input scope when no conversation is currently loaded (recovery path
    // after a failed load).
    const scope = this.effectiveScope();
    const previousId = this.conversationId();
    this.cancelTurn();
    this.loading.set(true);
    this.loadFailed.set(null);
    this.turnError.set(null);
    this.preflightError.set(null);
    this.history.set([]);
    this.conversationId.set(null);
    this.loadedScope.set(null);
    this.conversationInputTokens.set(0);
    this.conversationOutputTokens.set(0);

    this.api.create(scope).pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: payload => {
        this.applyConversationPayload(payload);
        // Reflect the new id into the URL so reload preserves the same thread. The effect's
        // override-vs-currentId guard suppresses the redundant fetch the URL change would
        // otherwise trigger.
        this.syncConversationOverrideInUrl(payload.conversation.id);
      },
      error: err => {
        this.loading.set(false);
        this.conversationId.set(previousId);
        this.loadFailed.set(formatHttpError(err));
      },
    });
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
    } else if (kind === 'propose_replay_with_edit') {
      this.applyReplayConfirmation(toolCallId);
    }
  }

  /** Dismiss the chip locally; no server call. Cached payloads are dropped. */
  protected onCancelToolCall(toolCallId: string): void {
    this.pendingSaves.delete(toolCallId);
    this.pendingRuns.delete(toolCallId);
    this.pendingReplays.delete(toolCallId);
    this.updateConfirmation(toolCallId, c => ({ ...c, state: 'cancelled' }));
  }

  /**
   * HAA-10: POSTs the cached package to the existing /api/workflows/package/apply endpoint
   * (WorkflowsWrite under the logged-in user) and reflects the outcome on the chip.
   *
   * Branches on the chip's `packageSource`:
   *  - `'inline'` (default): the LLM passed a `package` arg; we cached it and POST it directly.
   *  - `'draft'`: the LLM invoked save_workflow_package without args; the package lives in the
   *    conversation's workspace draft. Skip the inline preview (the tool already validated it
   *    server-side) and POST a tiny `{ conversationId }` body to /apply-from-draft, which reads
   *    the draft and applies it. Saves the round-trip payload through the browser too.
   */
  private applySaveConfirmation(toolCallId: string): void {
    const card = this.toolCalls().find(c => c.id === toolCallId);
    const conf = card?.confirmation;
    const isDraftSource = conf?.packageSource === 'draft';

    if (isDraftSource) {
      this.applyDraftSaveConfirmation(toolCallId);
      return;
    }

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
    this.workflowsApi.previewPackageImport(pkg).pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: preview => {
        if (!preview.canApply) {
          this.updateConfirmation(toolCallId, c => ({
            ...c,
            state: 'error',
            errorMessage: buildPackagePreviewFailureMessage(preview),
          }));
          return;
        }

        this.applyValidatedSaveConfirmation(toolCallId, pkg);
      },
      error: err => {
        this.updateConfirmation(toolCallId, c => ({
          ...c,
          state: 'error',
          errorMessage: `Save preview failed: ${formatHttpError(err)}`,
        }));
      },
    });
  }

  /**
   * Apply path for the draft-source chip: the package lives in the assistant conversation's
   * workspace as an immutable snapshot file (keyed by the `snapshotId` the tool result carried).
   * The server reads from the snapshot, not the live draft, so a draft mutation between preview
   * and confirm cannot redirect this Save to a different package.
   */
  private applyDraftSaveConfirmation(toolCallId: string): void {
    const conversationId = this.conversationId();
    if (!conversationId) {
      this.updateConfirmation(toolCallId, c => ({
        ...c,
        state: 'error',
        errorMessage: 'No active conversation — cannot save the workspace draft.',
      }));
      return;
    }

    const card = this.toolCalls().find(c => c.id === toolCallId);
    const snapshotId = card?.confirmation?.snapshotId;
    if (!snapshotId) {
      this.updateConfirmation(toolCallId, c => ({
        ...c,
        state: 'error',
        errorMessage: 'Snapshot id missing on this Save chip — re-ask the assistant to save the draft.',
      }));
      return;
    }

    this.updateConfirmation(toolCallId, c => ({ ...c, state: 'applying' }));
    this.workflowsApi.applyPackageImportFromDraft(conversationId, snapshotId).pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: result => {
        this.workflowsApi.getLatest(result.entryPoint.key).pipe(
          takeUntilDestroyed(this.destroyRef),
        ).subscribe({
          next: () => {
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
              errorMessage:
                `Save returned success, but CodeFlow could not load workflow `
                + `${result.entryPoint.key}: ${formatHttpError(err)}`,
            }));
          },
        });
      },
      error: err => {
        this.updateConfirmation(toolCallId, c => ({
          ...c,
          state: 'error',
          errorMessage: `Save from draft failed: ${formatHttpError(err)}`,
        }));
      },
    });
  }

  private applyValidatedSaveConfirmation(toolCallId: string, pkg: unknown): void {
    this.workflowsApi.applyPackageImport(pkg).pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: result => {
        this.workflowsApi.getLatest(result.entryPoint.key).pipe(
          takeUntilDestroyed(this.destroyRef),
        ).subscribe({
          next: () => {
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
              errorMessage:
                `Save returned success, but CodeFlow could not load workflow `
                + `${result.entryPoint.key}: ${formatHttpError(err)}`,
            }));
          },
        });
      },
      error: err => {
        this.updateConfirmation(toolCallId, c => ({
          ...c,
          state: 'error',
          errorMessage: `Save failed: ${formatHttpError(err)}`,
        }));
      },
    });
  }

  /**
   * HAA-13: POSTs the cached replay request to /api/traces/{id}/replay (TracesRead under the
   * logged-in user; the replay is read-only on the original saga). Surfaces the replay's
   * terminal state + a deep link to the trace inspector's Replay-with-Edit panel.
   */
  private applyReplayConfirmation(toolCallId: string): void {
    const cached = this.pendingReplays.get(toolCallId);
    if (!cached) {
      this.updateConfirmation(toolCallId, c => ({
        ...c,
        state: 'error',
        errorMessage: 'Replay request payload missing — ask the assistant to propose the replay again.',
      }));
      return;
    }
    this.updateConfirmation(toolCallId, c => ({ ...c, state: 'applying' }));
    this.tracesApi.replay(cached.originalTraceId, cached.request).pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: result => {
        this.pendingReplays.delete(toolCallId);
        // The replay endpoint returns 200 even when the replay finished as Failed / DriftRefused
        // / StepLimitExceeded — those are *replay outcomes*, not HTTP errors. Surface them on
        // the success banner with the right verbiage; the deep link is always usable so the
        // user can review the timeline regardless.
        this.updateConfirmation(toolCallId, c => ({
          ...c,
          state: 'success',
          applied: {
            kind: 'replay',
            originalTraceId: result.originalTraceId,
            replayState: result.replayState,
            replayTerminalPort: result.replayTerminalPort,
          },
        }));
      },
      error: err => {
        this.updateConfirmation(toolCallId, c => ({
          ...c,
          state: 'error',
          errorMessage: formatHttpError(err),
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
    this.tracesApi.create(req).pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
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
          errorMessage: formatHttpError(err),
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
        // HAA-17 — refresh the live token chip totals.
        this.conversationInputTokens.set(evt.conversationInputTokensTotal);
        this.conversationOutputTokens.set(evt.conversationOutputTokensTotal);
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
          } else if (evt.name === PROPOSE_REPLAY_WITH_EDIT_TOOL) {
            const cached = toReplayRequestCached(evt.arguments as Record<string, unknown>);
            if (cached) {
              this.pendingReplays.set(evt.id, cached);
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
          } else if (evt.name === PROPOSE_REPLAY_WITH_EDIT_TOOL) {
            confirmation = buildReplayConfirmationView(evt.result, this.pendingReplays.get(evt.id));
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
        this.surfaceDraftPackageSavePrompts(evt.message);
        this.pending.set(null);
        this.scrollToBottom();
        break;
      }
      case 'error': {
        this.turnError.set(evt.message);
        this.pending.set(null);
        break;
      }
      case 'preflight-refused': {
        // sc-274 phase 2 — server rejected the prompt before opening the SSE stream. Drop the
        // optimistic user bubble + the in-flight assistant pending so the UI doesn't show a
        // half-rendered turn that never happened, and surface the clarification banner.
        this.preflightError.set(evt.payload);
        this.optimisticUser.set(null);
        this.pending.set(null);
        this.streaming.set(false);
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
    this.loadedScope.set(null);
    this.history.set([]);
    this.loadFailed.set(null);
    this.turnError.set(null);
    this.preflightError.set(null);
    this.toolCalls.set([]);
    this.conversationInputTokens.set(0);
    this.conversationOutputTokens.set(0);
  }

  ngOnDestroy(): void {
    this.cancelTurn();
  }

  private loadConversation(scope: AssistantScope): void {
    this.loading.set(true);
    this.api.getOrCreate(scope).pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: payload => this.applyConversationPayload(payload),
      error: err => {
        this.loading.set(false);
        this.loadFailed.set(formatHttpError(err));
      },
    });
  }

  private loadConversationById(conversationId: string): void {
    this.loading.set(true);
    this.api.get(conversationId).pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: payload => this.applyConversationPayload(payload),
      error: err => {
        this.loading.set(false);
        this.loadFailed.set(formatHttpError(err));
      },
    });
  }

  private applyConversationPayload(payload: ConversationResponse): void {
    this.conversationId.set(payload.conversation.id);
    this.loadedScope.set(payload.conversation.scope);
    this.history.set(payload.messages);
    // HAA-17 — seed the live token chip with the persisted totals so reload-then-resume shows
    // the same numbers without waiting for the next streamed turn.
    this.conversationInputTokens.set(payload.conversation.inputTokensTotal ?? 0);
    this.conversationOutputTokens.set(payload.conversation.outputTokensTotal ?? 0);
    this.loading.set(false);
    // Initial-load scroll: the thread DOM isn't laid out yet when applyConversationPayload runs,
    // so a microtask fires before scrollHeight is meaningful. Defer to the next animation frame
    // so layout has settled. {force: true} routes through rAF; subsequent stream-event scrolls
    // (mid-turn) keep the cheap microtask path.
    this.scrollToBottom({ force: true });
  }

  /**
   * A well-behaved assistant should call `save_workflow_package`, whose preview result creates
   * the Save chip. In practice the model can also emit a complete `cf-workflow-package` block
   * and then talk as if the chip already exists. Keep the UI resilient by offering the same
   * confirmation directly from the drafted package. This does not depend on page context or
   * workspace selection.
   */
  private surfaceDraftPackageSavePrompts(message: AssistantMessage): void {
    if (message.role !== 'assistant') {
      return;
    }

    const packages = extractWorkflowPackagesFromMarkdown(message.content);
    if (packages.length === 0) {
      return;
    }

    const hasSaveToolCall = this.toolCalls().some(
      card => card.name === SAVE_WORKFLOW_PACKAGE_TOOL || card.confirmation?.kind === 'save_workflow_package',
    );
    if (hasSaveToolCall) {
      return;
    }

    const cards: ChatToolCallView[] = [];
    for (let i = 0; i < packages.length; i++) {
      const id = `draft-package:${message.id}:${i}`;
      const pkg = packages[i];
      const confirmation = buildDraftSaveConfirmationView(pkg);
      if (!confirmation) {
        continue;
      }
      this.pendingSaves.set(id, pkg);
      cards.push({
        id,
        name: 'draft_workflow_package',
        status: 'success',
        resultPreview: 'Workflow package detected in the assistant response. Confirm to save it to the library.',
        confirmation,
      });
    }

    if (cards.length > 0) {
      this.toolCalls.update(list => [...list, ...cards]);
    }
  }

  private syncConversationOverrideInUrl(conversationId: string): void {
    if (this.route.snapshot.queryParamMap.get('assistantConversation') === conversationId) {
      return;
    }

    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { assistantConversation: conversationId },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  private scrollToBottom(options?: { force?: boolean }): void {
    const apply = () => {
      const el = this.threadRef?.nativeElement;
      if (el) {
        el.scrollTop = el.scrollHeight;
      }
    };
    queueMicrotask(apply);
    if (options?.force) {
      // Initial conversation load: layout hasn't run yet on the just-rendered thread, so the
      // microtask sees scrollHeight === clientHeight. A second pass after the next frame
      // catches up once Angular has painted the new messages.
      requestAnimationFrame(apply);
    }
  }
}

/**
 * HAA-10: when the assistant's `save_workflow_package` tool returns a `preview_ok` verdict, the
 * chat-panel attaches a confirmation chip to the tool card so the user can authorize the save.
 * The cached package payload is held separately on the panel; this helper just produces the
 * view-model the chip renders.
 */
export function buildSaveConfirmationView(
  resultJson: string,
  pkg: unknown,
): ChatToolCallView['confirmation'] | undefined {
  let parsed:
    | {
        status?: unknown;
        packageSource?: unknown;
        snapshotId?: unknown;
        entryPoint?: { key?: unknown; version?: unknown };
      }
    | null = null;
  try {
    parsed = JSON.parse(resultJson);
  } catch {
    return undefined;
  }

  if (!parsed || parsed.status !== 'preview_ok') {
    return undefined;
  }

  // Two valid render paths:
  //  - inline: the LLM passed a `package` arg; we cached it and POST it on confirm.
  //  - draft: the LLM invoked save with no args; the package lives server-side in the
  //    conversation's workspace as an immutable snapshot file. We post `{ conversationId,
  //    snapshotId }` to `/api/workflows/package/apply-from-draft` so the apply binds to
  //    the exact bytes that were validated, not whatever the live draft happens to contain.
  const isDraftSource = parsed.packageSource === 'draft';
  if (!isDraftSource && !pkg) {
    return undefined;
  }

  // Draft path requires the snapshot id from the tool result. Without it the apply would
  // be ambiguous (live draft vs. validated bytes) — refuse to render the chip at all so the
  // user can't confirm a save that would be unsafe under draft mutation.
  const snapshotId = typeof parsed.snapshotId === 'string' && parsed.snapshotId.length > 0
    ? parsed.snapshotId
    : undefined;
  if (isDraftSource && !snapshotId) {
    return undefined;
  }

  const summary = pkg ? summarizeWorkflowPackage(pkg) : null;
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
    packageSource: isDraftSource ? 'draft' : 'inline',
    snapshotId,
    state: 'idle',
  };
}

export function buildDraftSaveConfirmationView(pkg: unknown): ChatToolCallView['confirmation'] | undefined {
  return buildSaveConfirmationView(JSON.stringify({ status: 'preview_ok' }), pkg);
}

export function buildPinnedMutationChip(card: ChatToolCallView): PinnedMutationChip | null {
  const conf = card.confirmation;
  if (!conf || conf.state === 'cancelled') {
    return null;
  }

  const base = {
    id: card.id,
    state: conf.state,
    confirmLabel: conf.confirmLabel ?? 'Confirm',
    cancelLabel: conf.cancelLabel ?? 'Cancel',
  };

  if (conf.state === 'idle') {
    return {
      ...base,
      title: conf.prompt,
      message: null,
      canConfirm: true,
      canCancel: true,
      linkUrl: null,
      linkLabel: null,
    };
  }

  if (conf.state === 'applying') {
    return {
      ...base,
      title: applyingTitle(conf.kind),
      message: 'Please keep this chat open while CodeFlow applies the change.',
      canConfirm: false,
      canCancel: false,
      linkUrl: null,
      linkLabel: null,
    };
  }

  if (conf.state === 'error') {
    return {
      ...base,
      title: failedTitle(conf.kind),
      message: conf.errorMessage ?? 'The request failed before CodeFlow returned details.',
      confirmLabel: 'Retry',
      cancelLabel: 'Dismiss',
      canConfirm: true,
      canCancel: true,
      linkUrl: null,
      linkLabel: null,
    };
  }

  if (conf.state === 'success' && conf.applied) {
    if (conf.applied.kind === 'workflow') {
      return {
        ...base,
        title: `Saved ${conf.applied.key} v${conf.applied.version} to the library.`,
        message: 'The workflow is ready to load from the workflow library.',
        canConfirm: false,
        canCancel: false,
        linkUrl: `/workflows/${encodeURIComponent(conf.applied.key)}`,
        linkLabel: 'Open workflow',
      };
    }

    if (conf.applied.kind === 'trace') {
      return {
        ...base,
        title: `Started trace ${conf.applied.traceId.slice(0, 8)}.`,
        message: null,
        canConfirm: false,
        canCancel: false,
        linkUrl: `/traces/${encodeURIComponent(conf.applied.traceId)}`,
        linkLabel: 'Open trace',
      };
    }

    return {
      ...base,
      title: `Replay ${conf.applied.replayState.toLowerCase()}.`,
      message: 'Open the trace inspector to review or refine in Replay-with-Edit.',
      canConfirm: false,
      canCancel: false,
      linkUrl: `/traces/${encodeURIComponent(conf.applied.originalTraceId)}`,
      linkLabel: 'Open trace',
    };
  }

  return null;
}

export function buildPackagePreviewFailureMessage(preview: WorkflowPackageImportPreview): string {
  const conflicts = preview.items.filter(item => item.action === 'Conflict');
  if (conflicts.length === 0) {
    return 'Workflow package cannot be saved as-is.';
  }

  const first = conflicts[0];
  const version = first.version === null || first.version === undefined ? '' : ` v${first.version}`;
  const remaining = conflicts.length > 1 ? ` (${conflicts.length - 1} more conflict${conflicts.length === 2 ? '' : 's'})` : '';
  return `Workflow package cannot be saved: ${first.kind} ${first.key}${version} conflicts. ${first.message}${remaining}`;
}

function applyingTitle(kind: NonNullable<ChatToolCallView['confirmation']>['kind']): string {
  switch (kind) {
    case 'save_workflow_package': return 'Saving workflow...';
    case 'run_workflow': return 'Starting workflow run...';
    case 'propose_replay_with_edit': return 'Starting replay...';
  }
}

function failedTitle(kind: NonNullable<ChatToolCallView['confirmation']>['kind']): string {
  switch (kind) {
    case 'save_workflow_package': return 'Workflow save failed.';
    case 'run_workflow': return 'Workflow run failed to start.';
    case 'propose_replay_with_edit': return 'Replay failed to start.';
  }
}

/**
 * HAA-11: when the assistant's `run_workflow` tool returns `preview_ok`, build the chip
 * view-model. The chip prompt names the workflow + version + supplied input keys so the user
 * sees what they're authorizing before clicking Run. The actual trace creation happens when the
 * user confirms (POST /api/traces); this helper just produces the chip view.
 */
export function buildRunConfirmationView(
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
export function toCreateTraceRequest(args: Record<string, unknown>): CreateTraceRequest | null {
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

/**
 * HAA-13: when the assistant's `propose_replay_with_edit` tool returns `preview_ok`, build the
 * chip view-model. The chip prompt names the trace + edit count + an opt-in to "force replay
 * past hard drift" hint when the call args set `force: true`. The replay itself happens when
 * the user confirms (POST /api/traces/{id}/replay) using the cached `ReplayRequest`.
 */
export function buildReplayConfirmationView(
  resultJson: string,
  cached: { originalTraceId: string; request: ReplayRequest } | undefined,
): ChatToolCallView['confirmation'] | undefined {
  let parsed: {
    status?: unknown;
    workflowKey?: unknown;
    edits?: Array<{ agentKey?: unknown; ordinal?: unknown }>;
    force?: unknown;
  } | null = null;
  try {
    parsed = JSON.parse(resultJson);
  } catch {
    return undefined;
  }

  if (!parsed || parsed.status !== 'preview_ok' || !cached) {
    return undefined;
  }

  const editCount = Array.isArray(parsed.edits) ? parsed.edits.length : 0;
  const workflowPart = typeof parsed.workflowKey === 'string' ? ` ${parsed.workflowKey}` : '';
  const editsPart = editCount === 1 ? '1 edit' : `${editCount} edits`;
  const forcePart = parsed.force === true ? ' (force past drift)' : '';
  const prompt = `Replay${workflowPart} with ${editsPart}${forcePart}?`;

  return {
    kind: 'propose_replay_with_edit',
    prompt,
    confirmLabel: 'Replay',
    cancelLabel: 'Cancel',
    state: 'idle',
  };
}

/**
 * HAA-13: parse a `propose_replay_with_edit` tool-call's structured arguments into the cached
 * `ReplayRequest` shape `TracesApi.replay()` expects, paired with the original trace id (used
 * for the URL). Returns null if `traceId` / `edits` aren't present.
 */
export function toReplayRequestCached(
  args: Record<string, unknown>,
): { originalTraceId: string; request: ReplayRequest } | null {
  const traceId = typeof args['traceId'] === 'string' ? (args['traceId'] as string) : '';
  const edits = Array.isArray(args['edits']) ? (args['edits'] as Array<Record<string, unknown>>) : [];
  if (!traceId || edits.length === 0) {
    return null;
  }

  const mapped = edits.map(e => ({
    agentKey: typeof e['agentKey'] === 'string' ? (e['agentKey'] as string) : '',
    ordinal: typeof e['ordinal'] === 'number' ? (e['ordinal'] as number) : 0,
    decision: typeof e['decision'] === 'string' ? (e['decision'] as string) : null,
    output: typeof e['output'] === 'string' ? (e['output'] as string) : null,
    payload: e['payload'] ?? null,
  }));

  const request: ReplayRequest = {
    edits: mapped.filter(e => e.agentKey && e.ordinal > 0),
  };
  if (args['force'] === true) request.force = true;
  if (typeof args['workflowVersionOverride'] === 'number') {
    request.workflowVersionOverride = args['workflowVersionOverride'] as number;
  }
  if (request.edits!.length === 0) {
    return null;
  }

  return { originalTraceId: traceId, request };
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
