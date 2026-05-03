import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter, map, startWith } from 'rxjs/operators';
import { ChatPanelComponent } from '../ui/chat';
import { IconComponent } from '../ui/icon.component';
import { PageContextService } from '../core/page-context.service';
import { ThemeService } from '../core/theme.service';
import { AssistantHistoryComponent } from './assistant-history.component';

type SidebarTab = 'assistant' | 'history';

/**
 * HAA-7: Right-rail assistant sidebar. Available across the app.
 *
 * Reads {@link PageContextService} to resolve the current page's context, but keeps the chat
 * mounted against the global homepage conversation so a thread started on the home page follows
 * the user into agents, workflows, traces, and other authoring surfaces. The page context is
 * still forwarded per turn so the assistant can reason about the current screen.
 *
 * Tabs split the docked sidebar into Assistant (the live chat panel) and History (the user's
 * recent conversations). Selecting a row in History routes to the conversation's scope and
 * flips back to the Assistant tab so the just-selected thread is in front of the user.
 */
@Component({
  selector: 'cf-assistant-sidebar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChatPanelComponent, IconComponent, AssistantHistoryComponent],
  template: `
    @if (scope(); as resolvedScope) {
      <aside
        class="sidebar"
        [attr.data-mode]="mode()"
        [attr.data-collapsed]="collapsed() ? 'true' : 'false'"
        [attr.aria-label]="'Assistant sidebar'"
        data-testid="assistant-sidebar"
      >
        @if (collapsed()) {
          <button
            type="button"
            class="sidebar-rail"
            (click)="theme.setAssistantSidebarMode('docked')"
            title="Open assistant"
            data-testid="assistant-sidebar-expand"
          >
            <cf-icon name="bot"></cf-icon>
            <span class="sidebar-rail-label">Assistant</span>
          </button>
        } @else {
          <div class="sidebar-header" role="tablist" aria-label="Assistant sidebar tabs">
            <div class="sidebar-tabs tabs">
              <button
                type="button"
                role="tab"
                class="tab"
                [attr.data-active]="tab() === 'assistant' ? 'true' : null"
                [attr.aria-selected]="tab() === 'assistant' ? 'true' : 'false'"
                (click)="setTab('assistant')"
                data-testid="assistant-sidebar-tab-assistant"
              >Assistant</button>
              <button
                type="button"
                role="tab"
                class="tab"
                [attr.data-active]="tab() === 'history' ? 'true' : null"
                [attr.aria-selected]="tab() === 'history' ? 'true' : 'false'"
                (click)="setTab('history')"
                data-testid="assistant-sidebar-tab-history"
              >History</button>
            </div>
            <div class="sidebar-actions" aria-label="Assistant display controls">
              <button
                type="button"
                class="sidebar-action"
                (click)="toggleExpanded()"
                [title]="expanded() ? 'Dock assistant' : 'Expand assistant'"
                [attr.aria-label]="expanded() ? 'Dock assistant' : 'Expand assistant'"
                [attr.aria-pressed]="expanded() ? 'true' : 'false'"
                data-testid="assistant-sidebar-mode-toggle"
              >
                <cf-icon [name]="expanded() ? 'minimize' : 'maximize'"></cf-icon>
              </button>
              <button
                type="button"
                class="sidebar-action"
                (click)="theme.setAssistantSidebarCollapsed(true)"
                [title]="'Collapse assistant — ' + scopeLabel()"
                [attr.aria-label]="'Collapse assistant'"
                data-testid="assistant-sidebar-collapse"
              >
                <cf-icon name="panelL"></cf-icon>
              </button>
            </div>
          </div>
          <div class="sidebar-body">
            <!--
              The chat panel stays mounted across tab switches so an in-flight stream isn't
              cancelled when the user peeks at History. CSS hides the inactive tab's body
              instead of @if-tearing it down.
            -->
            <div class="tab-pane" [attr.data-active]="tab() === 'assistant' ? 'true' : null">
              <cf-chat-panel
                [scope]="resolvedScope"
                [pageContext]="pageContext.current()"
                [conversationIdOverride]="selectedConversationId()"
              />
            </div>
            <div class="tab-pane" [attr.data-active]="tab() === 'history' ? 'true' : null">
              @if (tab() === 'history') {
                <cf-assistant-history (selected)="onHistorySelected()" />
              }
            </div>
          </div>
        }
      </aside>
    }
  `,
  styles: [`
    :host {
      display: contents;
    }
    .sidebar {
      display: flex;
      flex-direction: column;
      flex: 0 0 400px;
      align-self: stretch;
      min-height: 0;
      height: 100%;
      max-height: 100%;
      overflow: hidden;
      background: var(--surface, #131519);
      border-left: 1px solid var(--border, rgba(255,255,255,0.08));
      width: 400px;
      transition: width 120ms ease, flex-basis 120ms ease;
    }
    .sidebar[data-collapsed='true'] {
      flex-basis: 44px;
      width: 44px;
    }
    .sidebar[data-mode='expanded'] {
      flex-basis: 100%;
      width: 100%;
      border-left-color: transparent;
    }
    .sidebar-rail {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 8px;
      padding: 14px 6px;
      background: transparent;
      border: 0;
      color: var(--text-muted, #9aa3b2);
      cursor: pointer;
      width: 100%;
      flex: 0 0 auto;
    }
    .sidebar-rail:hover {
      color: var(--text, #E7E9EE);
      background: var(--surface-hover, rgba(255,255,255,0.03));
    }
    .sidebar-rail-label {
      writing-mode: vertical-rl;
      transform: rotate(180deg);
      font-size: 10px;
      letter-spacing: 0.08em;
      text-transform: uppercase;
    }
    .sidebar-header {
      display: flex;
      align-items: stretch;
      justify-content: space-between;
      gap: 8px;
      padding: 0 8px;
      border-bottom: 1px solid var(--border, rgba(255,255,255,0.08));
      flex: 0 0 auto;
    }
    .sidebar-tabs {
      flex: 1 1 auto;
      min-width: 0;
      border-bottom: 0;
    }
    .sidebar-tabs .tab {
      padding: 10px 12px;
    }
    .sidebar-actions {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 4px 0;
    }
    .sidebar-action {
      background: transparent;
      border: 0;
      color: var(--text-muted, #9aa3b2);
      cursor: pointer;
      padding: 4px;
      border-radius: 4px;
      display: inline-flex;
    }
    .sidebar-action:hover {
      color: var(--text, #E7E9EE);
      background: var(--surface-hover, rgba(255,255,255,0.04));
    }
    .sidebar-body {
      position: relative;
      flex: 1 1 auto;
      min-height: 0;
      overflow: hidden;
      display: flex;
      flex-direction: column;
    }
    .tab-pane {
      display: none;
      flex: 1 1 auto;
      min-height: 0;
      flex-direction: column;
    }
    .tab-pane[data-active='true'] {
      display: flex;
    }
    .sidebar-body cf-chat-panel {
      flex: 1 1 auto;
      display: flex;
      flex-direction: column;
      min-height: 0;
      height: 100%;
    }
  `],
})
export class AssistantSidebarComponent {
  protected readonly pageContext = inject(PageContextService);
  protected readonly theme = inject(ThemeService);
  private readonly router = inject(Router);
  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map(e => e.urlAfterRedirects),
      startWith(this.router.url),
    ),
    { initialValue: this.router.url },
  );

  // The sidebar shares the home page's assistant conversation across all shell pages; page
  // context travels separately through the chat panel's per-turn PageContext input.
  protected readonly scope = computed(() => ({ kind: 'homepage' as const }));
  protected readonly mode = computed(() => this.theme.assistantSidebarMode());
  protected readonly collapsed = computed(() => this.mode() === 'collapsed');
  protected readonly expanded = computed(() => this.mode() === 'expanded');
  protected readonly selectedConversationId = computed(() => {
    const value = this.router.parseUrl(this.url()).queryParams['assistantConversation'];
    return typeof value === 'string' && value ? value : null;
  });

  private readonly tabSignal = signal<SidebarTab>('assistant');
  protected readonly tab = this.tabSignal.asReadonly();

  protected readonly scopeLabel = computed(() => {
    const ctx = this.pageContext.current();
    switch (ctx.kind) {
      case 'home': return 'Assistant';
      case 'trace': return 'Trace assistant';
      case 'workflow-editor': return 'Workflow assistant';
      case 'agent-editor': return 'Agent assistant';
      case 'library': return 'Library assistant';
      case 'traces-list': return 'Assistant';
      case 'other': return 'Assistant';
    }
  });

  protected toggleExpanded(): void {
    this.theme.setAssistantSidebarMode(this.expanded() ? 'docked' : 'expanded');
  }

  protected setTab(next: SidebarTab): void {
    this.tabSignal.set(next);
  }

  protected onHistorySelected(): void {
    this.tabSignal.set('assistant');
  }
}
