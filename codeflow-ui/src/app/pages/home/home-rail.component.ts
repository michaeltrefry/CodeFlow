import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, Input, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { catchError, finalize, of } from 'rxjs';
import {
  AssistantApi,
  AssistantConversationSummary,
  AssistantTokenUsageSummary,
} from '../../core/assistant.api';
import { relativeTime } from '../../core/format-time';
import { TracesApi } from '../../core/traces.api';
import { RecentWorkflow, WorkflowsApi } from '../../core/workflows.api';
import { TraceSummary } from '../../core/models';
import { ChipComponent } from '../../ui/chip.component';

/**
 * HAA-14 — Homepage context rail. Fills the right-side region of {@link HomeComponent} with
 * three live sections (resume conversations, recent traces, recently used workflows) plus an
 * assistant-token chip header. The rail is intentionally lightweight: each section paints an
 * empty state when its data source returns nothing, and any individual fetch failure renders
 * a muted error message rather than collapsing the whole rail.
 *
 * The recent-traces section is intentionally not user-scoped today — `WorkflowSagas` doesn't
 * carry an initiating-user column yet, so the rail surfaces the most-recent N globally. Once
 * a user column lands, only the backend query needs to change; the rail consumes the same
 * `TraceSummary[]` shape.
 *
 * The rail is hidden in demo mode for sections that require real-data tools (recent traces /
 * recently-used workflows). The resume-conversation section still renders so demo users can
 * tell their single homepage thread is being persisted.
 */
@Component({
  selector: 'cf-home-rail',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, RouterLink, ChipComponent],
  template: `
    @if (assistantTokenChip(); as chip) {
      <section class="rail-section rail-token-chip" data-testid="rail-token-chip">
        <span class="section-eyebrow">Assistant tokens · today</span>
        <div class="token-line">
          <span class="token-value mono">{{ chip.todayInput | number }} in</span>
          <span class="token-sep">·</span>
          <span class="token-value mono">{{ chip.todayOutput | number }} out</span>
        </div>
        @if (chip.allTimeCallCount > 0) {
          <span class="section-eyebrow muted">{{ chip.allTimeCallCount | number }} all-time calls</span>
        }
      </section>
    }

    <!-- Resume conversations -->
    <section class="rail-section" data-testid="rail-section-resume">
      <header class="section-head">
        <span class="section-eyebrow">Resume</span>
        <h3 class="section-title">Your conversations</h3>
      </header>
      @if (conversationsLoading()) {
        <p class="muted xs">Loading…</p>
      } @else if (conversationsError()) {
        <p class="muted xs">{{ conversationsError() }}</p>
      } @else if (resumableConversations().length === 0) {
        <p class="muted xs" data-testid="rail-resume-empty">
          You don't have any saved threads yet — start one in the chat.
        </p>
      } @else {
        <ul class="rail-list">
          @for (c of resumableConversations(); track c.id) {
            <li>
              <a
                class="rail-link"
                [routerLink]="conversationRouteFor(c)"
                [queryParams]="conversationQueryParamsFor(c)"
                [attr.data-testid]="'rail-resume-' + c.id">
                <span class="rail-link-label">{{ conversationLabelFor(c) }}</span>
                <span class="rail-link-meta mono xs">{{ scopeBadgeFor(c) }} · {{ relativeTime(c.updatedAtUtc) }}</span>
              </a>
            </li>
          }
        </ul>
      }
    </section>

    <!-- Recent traces -->
    @if (!demoMode) {
      <section class="rail-section" data-testid="rail-section-recent-traces">
        <header class="section-head">
          <span class="section-eyebrow">Recent</span>
          <h3 class="section-title">Traces</h3>
        </header>
        @if (tracesLoading()) {
          <p class="muted xs">Loading…</p>
        } @else if (tracesError()) {
          <p class="muted xs">{{ tracesError() }}</p>
        } @else if (recentTraces().length === 0) {
          <p class="muted xs" data-testid="rail-traces-empty">
            No traces yet. Run a workflow and it'll show up here.
          </p>
        } @else {
          <ul class="rail-list">
            @for (t of recentTraces(); track t.traceId) {
              <li>
                <a class="rail-link" [routerLink]="['/traces', t.traceId]" [attr.data-testid]="'rail-trace-' + t.traceId">
                  <span class="rail-link-label">{{ t.workflowKey }}</span>
                  <span class="rail-link-meta mono xs">
                    <cf-chip [variant]="stateVariantFor(t.currentState)" dot>{{ t.currentState }}</cf-chip>
                    {{ relativeTime(t.updatedAtUtc) }}
                  </span>
                </a>
              </li>
            }
          </ul>
        }
      </section>
    }

    <!-- Recently used workflows -->
    @if (!demoMode) {
      <section class="rail-section" data-testid="rail-section-recent-workflows">
        <header class="section-head">
          <span class="section-eyebrow">Library</span>
          <h3 class="section-title">Recently used</h3>
        </header>
        @if (workflowsLoading()) {
          <p class="muted xs">Loading…</p>
        } @else if (workflowsError()) {
          <p class="muted xs">{{ workflowsError() }}</p>
        } @else if (recentWorkflows().length === 0) {
          <p class="muted xs" data-testid="rail-workflows-empty">
            Your library is empty — import or author a workflow to get started.
          </p>
        } @else {
          <ul class="rail-list">
            @for (w of recentWorkflows(); track w.summary.key) {
              <li>
                <a
                  class="rail-link"
                  [routerLink]="['/workflows', w.summary.key]"
                  [attr.data-testid]="'rail-workflow-' + w.summary.key">
                  <span class="rail-link-label">{{ w.summary.name }}</span>
                  <span class="rail-link-meta mono xs">
                    v{{ w.summary.latestVersion }} · {{ relativeTime(w.lastUsedAtUtc) }}
                  </span>
                </a>
              </li>
            }
          </ul>
        }
      </section>
    }
  `,
  styles: [`
    :host {
      display: flex;
      flex-direction: column;
      gap: 18px;
    }
    .rail-section {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .rail-token-chip {
      padding: 10px 12px;
      border: 1px solid var(--border, rgba(255,255,255,0.08));
      border-radius: var(--radius-sm, 6px);
      background: var(--surface-2, rgba(255,255,255,0.03));
      gap: 4px;
    }
    .token-line {
      display: flex;
      align-items: baseline;
      gap: 8px;
      font-size: var(--fs-md, 13px);
      color: var(--text, #E7E9EE);
    }
    .token-value { font-weight: 600; }
    .token-sep { color: var(--text-muted, #9aa3b2); }
    .section-head { display: flex; flex-direction: column; gap: 2px; }
    .section-eyebrow {
      font-size: 11px;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: var(--text-muted, #9aa3b2);
    }
    .section-eyebrow.muted { color: var(--text-muted, #9aa3b2); opacity: 0.75; }
    .section-title {
      margin: 0;
      font-size: var(--fs-md, 13px);
      font-weight: 600;
      color: var(--text, #E7E9EE);
    }
    .rail-list {
      list-style: none;
      padding: 0;
      margin: 0;
      display: flex;
      flex-direction: column;
      gap: 4px;
    }
    .rail-link {
      display: flex;
      flex-direction: column;
      gap: 2px;
      padding: 6px 8px;
      border-radius: var(--radius-sm, 6px);
      text-decoration: none;
      color: var(--text, #E7E9EE);
      transition: background 120ms ease;
    }
    .rail-link:hover { background: var(--surface-2, rgba(255,255,255,0.04)); }
    .rail-link-label {
      font-size: var(--fs-md, 13px);
      line-height: 1.35;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .rail-link-meta {
      display: flex;
      align-items: center;
      gap: 6px;
      color: var(--text-muted, #9aa3b2);
      font-size: 11px;
    }
    .muted { color: var(--text-muted, #9aa3b2); }
    .xs { font-size: 11px; }
    .mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
  `],
})
export class HomeRailComponent implements OnInit {
  /** When true, anonymous demo-mode user — hide sections that require real data tools. */
  @Input() demoMode = false;

  private readonly assistantApi = inject(AssistantApi);
  private readonly tracesApi = inject(TracesApi);
  private readonly workflowsApi = inject(WorkflowsApi);
  private readonly destroyRef = inject(DestroyRef);

  // Signals: each section tracks its own loading + error so a single fetch failure doesn't
  // collapse the whole rail. Empty-state branches read from the loaded data signals.
  private readonly conversations = signal<AssistantConversationSummary[]>([]);
  private readonly tokenSummary = signal<AssistantTokenUsageSummary | null>(null);
  private readonly recentTracesData = signal<TraceSummary[]>([]);
  private readonly recentWorkflowsData = signal<RecentWorkflow[]>([]);

  protected readonly conversationsLoading = signal(false);
  protected readonly tracesLoading = signal(false);
  protected readonly workflowsLoading = signal(false);

  protected readonly conversationsError = signal<string | null>(null);
  protected readonly tracesError = signal<string | null>(null);
  protected readonly workflowsError = signal<string | null>(null);

  /**
   * "Resumable" filters out empty homepage threads — a fresh first-visit get-or-create lands a
   * conversation row in the DB but no messages yet, and showing it as "Homepage · 0 messages"
   * is noise. Entity-scoped threads always render (their existence already implies intent).
   */
  protected readonly resumableConversations = computed(() =>
    this.conversations().filter(
      c => c.scope.kind !== 'homepage' || c.messageCount > 0,
    ),
  );

  protected readonly recentTraces = computed(() => this.recentTracesData().slice(0, 5));
  protected readonly recentWorkflows = computed(() => this.recentWorkflowsData());

  /**
   * Token chip data. Returns null when there's nothing to show so the template can suppress the
   * chip entirely (a brand-new user hasn't issued any LLM calls yet).
   */
  protected readonly assistantTokenChip = computed(() => {
    const summary = this.tokenSummary();
    if (!summary) return null;

    const todayInput = pickInputTokens(summary.today.totals);
    const todayOutput = pickOutputTokens(summary.today.totals);
    if (summary.today.callCount === 0 && summary.allTime.callCount === 0) {
      return null;
    }

    return {
      todayInput,
      todayOutput,
      allTimeCallCount: summary.allTime.callCount,
    };
  });

  ngOnInit(): void {
    this.loadConversations();
    this.loadTokenSummary();
    if (!this.demoMode) {
      this.loadRecentTraces();
      this.loadRecentWorkflows();
    }
  }

  private loadConversations(): void {
    this.conversationsLoading.set(true);
    this.conversationsError.set(null);
    this.assistantApi
      .listConversations(20)
      .pipe(
        catchError(err => {
          this.conversationsError.set(err?.error?.error ?? err?.message ?? 'Failed to load conversations');
          return of({ conversations: [] });
        }),
        finalize(() => this.conversationsLoading.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(response => this.conversations.set(response.conversations));
  }

  private loadTokenSummary(): void {
    this.assistantApi
      .getTokenUsageSummary()
      .pipe(
        catchError(() => of(null as AssistantTokenUsageSummary | null)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(summary => this.tokenSummary.set(summary));
  }

  private loadRecentTraces(): void {
    this.tracesLoading.set(true);
    this.tracesError.set(null);
    this.tracesApi
      .list()
      .pipe(
        catchError(err => {
          this.tracesError.set(err?.error?.error ?? err?.message ?? 'Failed to load traces');
          return of([] as TraceSummary[]);
        }),
        finalize(() => this.tracesLoading.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(traces => this.recentTracesData.set(traces));
  }

  private loadRecentWorkflows(): void {
    this.workflowsLoading.set(true);
    this.workflowsError.set(null);
    this.workflowsApi
      .listRecent(5)
      .pipe(
        catchError(err => {
          this.workflowsError.set(err?.error?.error ?? err?.message ?? 'Failed to load workflows');
          return of([] as RecentWorkflow[]);
        }),
        finalize(() => this.workflowsLoading.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(workflows => this.recentWorkflowsData.set(workflows));
  }

  protected conversationLabelFor(c: AssistantConversationSummary): string {
    if (c.firstUserMessagePreview) return c.firstUserMessagePreview;
    if (c.scope.kind === 'entity' && c.scope.entityType && c.scope.entityId) {
      return `${c.scope.entityType} · ${c.scope.entityId}`;
    }
    return 'Homepage thread';
  }

  protected scopeBadgeFor(c: AssistantConversationSummary): string {
    if (c.scope.kind === 'homepage') return 'Homepage';
    return c.scope.entityType ?? 'Entity';
  }

  /**
   * Routes for resume links. Homepage threads stay on `/`; entity-scoped threads drop the user
   * onto the entity's primary detail page so the sidebar (HAA-7) re-attaches the conversation.
   * For now we only know how to deep-link to traces and workflows; other entity types fall back
   * to the homepage so the link is at least navigable.
   */
  protected conversationRouteFor(c: AssistantConversationSummary): unknown[] {
    if (c.scope.kind === 'homepage') return ['/'];
    switch (c.scope.entityType) {
      case 'trace':
        return c.scope.entityId ? ['/traces', c.scope.entityId] : ['/traces'];
      case 'workflow':
        return c.scope.entityId ? ['/workflows', c.scope.entityId] : ['/workflows'];
      default:
        return ['/'];
    }
  }

  protected conversationQueryParamsFor(c: AssistantConversationSummary): Record<string, string> {
    return { assistantConversation: c.id };
  }

  protected stateVariantFor(state: string): 'ok' | 'warn' | 'err' | 'running' {
    switch (state) {
      case 'Completed':
        return 'ok';
      case 'Failed':
        return 'err';
      case 'Running':
        return 'running';
      default:
        return 'warn';
    }
  }

  protected relativeTime = relativeTime;
}

/**
 * Provider-agnostic "input tokens" extraction. Different providers report under different keys
 * (`input_tokens`, `prompt_tokens`); we sum any matching field. Same approach as the existing
 * token-usage-aggregator which is schema-less by design.
 */
function pickInputTokens(totals: Record<string, number>): number {
  let sum = 0;
  for (const [key, value] of Object.entries(totals)) {
    if (key === 'input_tokens' || key === 'prompt_tokens') sum += value;
  }
  return sum;
}

function pickOutputTokens(totals: Record<string, number>): number {
  let sum = 0;
  for (const [key, value] of Object.entries(totals)) {
    if (key === 'output_tokens' || key === 'completion_tokens') sum += value;
  }
  return sum;
}
