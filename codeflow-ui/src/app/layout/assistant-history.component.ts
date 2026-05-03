import { CommonModule } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  EventEmitter,
  OnInit,
  Output,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { catchError, finalize, of } from 'rxjs';
import { AssistantApi, AssistantConversationSummary } from '../core/assistant.api';
import { relativeTime } from '../core/format-time';

/**
 * History tab body for the assistant sidebar. Lists the caller's recent assistant conversations
 * so a thread that was started elsewhere can be picked up from any page. Selecting a row routes
 * to the entity the conversation is anchored to and sets `?assistantConversation=<id>` so the
 * sidebar's chat panel hydrates that thread on the next render.
 *
 * Empty homepage threads (no user message yet) are filtered out so the list doesn't fill with
 * never-used get-or-create rows. The same filter the home rail used before this view replaced it.
 */
@Component({
  selector: 'cf-assistant-history',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, RouterLink],
  template: `
    <div class="history" data-testid="assistant-history">
      @if (loading()) {
        <p class="muted xs" data-testid="assistant-history-loading">Loading…</p>
      } @else if (errorMessage()) {
        <p class="muted xs" data-testid="assistant-history-error">{{ errorMessage() }}</p>
      } @else if (resumable().length === 0) {
        <p class="muted xs" data-testid="assistant-history-empty">
          You don't have any saved threads yet — start one in the Assistant tab.
        </p>
      } @else {
        <ul class="history-list">
          @for (c of resumable(); track c.id) {
            <li>
              <a
                class="history-link"
                [routerLink]="routeFor(c)"
                [queryParams]="queryParamsFor(c)"
                queryParamsHandling="merge"
                (click)="selected.emit(c)"
                [attr.data-testid]="'assistant-history-row-' + c.id"
              >
                <span class="history-link-label">{{ labelFor(c) }}</span>
                <span class="history-link-meta mono xs">
                  {{ scopeBadgeFor(c) }} · {{ relativeTime(c.updatedAtUtc) }}
                </span>
              </a>
            </li>
          }
        </ul>
      }
    </div>
  `,
  styles: [`
    :host {
      display: flex;
      flex-direction: column;
      flex: 1 1 auto;
      min-height: 0;
      overflow-y: auto;
    }
    .history {
      display: flex;
      flex-direction: column;
      gap: 8px;
      padding: 12px;
    }
    .history-list {
      list-style: none;
      padding: 0;
      margin: 0;
      display: flex;
      flex-direction: column;
      gap: 4px;
    }
    .history-link {
      display: flex;
      flex-direction: column;
      gap: 2px;
      padding: 8px 10px;
      border-radius: var(--radius-sm, 6px);
      text-decoration: none;
      color: var(--text, #E7E9EE);
      transition: background 120ms ease;
    }
    .history-link:hover { background: var(--surface-2, rgba(255,255,255,0.04)); }
    .history-link-label {
      font-size: var(--fs-md, 13px);
      line-height: 1.35;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .history-link-meta {
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
export class AssistantHistoryComponent implements OnInit {
  /** Emitted after the user clicks a conversation row, so the sidebar can flip back to Assistant. */
  @Output() readonly selected = new EventEmitter<AssistantConversationSummary>();

  private readonly assistantApi = inject(AssistantApi);
  private readonly destroyRef = inject(DestroyRef);

  private readonly conversations = signal<AssistantConversationSummary[]>([]);
  protected readonly loading = signal(false);
  protected readonly errorMessage = signal<string | null>(null);

  protected readonly resumable = computed(() =>
    this.conversations().filter(
      c => c.scope.kind !== 'homepage' || c.messageCount > 0,
    ),
  );

  protected readonly relativeTime = relativeTime;

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.errorMessage.set(null);
    this.assistantApi
      .listConversations(20)
      .pipe(
        catchError(err => {
          this.errorMessage.set(err?.error?.error ?? err?.message ?? 'Failed to load conversations');
          return of({ conversations: [] });
        }),
        finalize(() => this.loading.set(false)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(response => this.conversations.set(response.conversations));
  }

  protected labelFor(c: AssistantConversationSummary): string {
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

  protected routeFor(c: AssistantConversationSummary): unknown[] {
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

  protected queryParamsFor(c: AssistantConversationSummary): Record<string, string> {
    return { assistantConversation: c.id };
  }
}
