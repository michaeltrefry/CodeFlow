import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter, map, startWith } from 'rxjs/operators';
import { ChatPanelComponent } from '../ui/chat';
import { IconComponent } from '../ui/icon.component';
import { PageContextService } from '../core/page-context.service';
import { ThemeService } from '../core/theme.service';

/**
 * HAA-7: Right-rail assistant sidebar. Available across the app.
 *
 * Reads {@link PageContextService} to resolve the current page's context, but keeps the chat
 * mounted against the global homepage conversation so a thread started on the home page follows
 * the user into agents, workflows, traces, and other authoring surfaces. The page context is
 * still forwarded per turn so the assistant can reason about the current screen.
 *
 * The sidebar owns the assistant surface everywhere in the authenticated shell, including home.
 * The home page no longer mounts its own chat panel, so this keeps one durable assistant thread
 * without duplicate token-spending surfaces.
 */
@Component({
  selector: 'cf-assistant-sidebar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChatPanelComponent, IconComponent],
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
          <div class="sidebar-body">
            <cf-chat-panel
              [scope]="resolvedScope"
              [pageContext]="pageContext.current()"
              [conversationIdOverride]="selectedConversationId()"
            />
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
    /* The chat panel renders its own header (title, conversation id). Sidebar controls sit
       in the top-right as a compact overlay so the chat panel keeps full vertical real estate
       without the sidebar duplicating its title. */
    .sidebar-actions {
      position: absolute;
      top: 8px;
      right: 8px;
      z-index: 1;
      display: inline-flex;
      gap: 4px;
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
    .sidebar:not([data-collapsed='true']) {
      position: relative;
    }
    .sidebar-body {
      flex: 1 1 auto;
      min-height: 0;
      overflow: hidden;
      display: flex;
      flex-direction: column;
    }
    .sidebar-body cf-chat-panel {
      --chat-panel-head-padding-right: 74px;
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
}
