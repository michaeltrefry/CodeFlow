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
 * Suppresses itself on the home page — the home page's main pane already mounts the chat in a
 * larger layout, so a parallel sidebar would just show the same conversation twice.
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
        [attr.data-collapsed]="collapsed() ? 'true' : 'false'"
        [attr.aria-label]="'Assistant sidebar'"
        data-testid="assistant-sidebar"
      >
        @if (collapsed()) {
          <button
            type="button"
            class="sidebar-rail"
            (click)="theme.setAssistantSidebarCollapsed(false)"
            title="Open assistant"
            data-testid="assistant-sidebar-expand"
          >
            <cf-icon name="bot"></cf-icon>
            <span class="sidebar-rail-label">Assistant</span>
          </button>
        } @else {
          <button
            type="button"
            class="sidebar-collapse"
            (click)="theme.setAssistantSidebarCollapsed(true)"
            [title]="'Collapse assistant — ' + scopeLabel()"
            [attr.aria-label]="'Collapse assistant'"
            data-testid="assistant-sidebar-collapse"
          >
            <cf-icon name="panelL"></cf-icon>
          </button>
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
      flex: 0 0 360px;
      min-height: 0;
      height: 100%;
      max-height: 100%;
      overflow: hidden;
      background: var(--surface, #131519);
      border-left: 1px solid var(--border, rgba(255,255,255,0.08));
      width: 360px;
      transition: width 120ms ease;
    }
    .sidebar[data-collapsed='true'] {
      flex-basis: 44px;
      width: 44px;
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
    /* The chat panel renders its own header (title, conversation id). The collapse button sits
       in the sidebar's top-right as a compact icon overlay so the chat panel keeps full
       vertical real estate without the sidebar duplicating its title. */
    .sidebar-collapse {
      position: absolute;
      top: 8px;
      right: 8px;
      z-index: 1;
      background: transparent;
      border: 0;
      color: var(--text-muted, #9aa3b2);
      cursor: pointer;
      padding: 4px;
      border-radius: 4px;
      display: inline-flex;
    }
    .sidebar-collapse:hover {
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
      --chat-panel-head-padding-right: 44px;
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

  // The sidebar shares the home page's assistant conversation across all non-home pages; page
  // context travels separately through the chat panel's per-turn PageContext input.
  protected readonly scope = computed(() =>
    this.pageContext.current().kind === 'home' ? null : { kind: 'homepage' as const },
  );
  protected readonly collapsed = computed(() => this.theme.assistantSidebarCollapsed());
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
}
