import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { ChatPanelComponent } from '../ui/chat';
import { IconComponent } from '../ui/icon.component';
import { PageContextService } from '../core/page-context.service';
import { pageContextToScope } from '../core/page-context';
import { ThemeService } from '../core/theme.service';

/**
 * HAA-7: Right-rail assistant sidebar. Available across the app, scoped per page.
 *
 * Reads {@link PageContextService} to resolve the current page's context, derives an
 * `AssistantScope` via {@link pageContextToScope}, and mounts the HAA-2 chat panel keyed by
 * that scope. Switching from trace A to trace B remounts the chat against a different
 * conversation; returning to A resumes the original via the backend's `(userId, scopeKey)`
 * keying. Collapsed/expanded state persists per-user via {@link ThemeService}.
 *
 * Suppresses itself on the home page — the home page's main pane already mounts the chat in a
 * larger layout, so a parallel sidebar would just show the same conversation twice. Likewise,
 * if the resolved scope is null (currently only `home`), the sidebar renders nothing.
 *
 * HAA-8 will inject PageContext details into the prompt and surface suggestion chips; this
 * slice ships only the surface + scope routing.
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
            <cf-chat-panel [scope]="resolvedScope" />
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
      min-height: 0;
      background: var(--surface, #131519);
      border-left: 1px solid var(--border, rgba(255,255,255,0.08));
      width: 360px;
      transition: width 120ms ease;
    }
    .sidebar[data-collapsed='true'] {
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
      display: flex;
      flex-direction: column;
    }
    .sidebar-body cf-chat-panel {
      flex: 1 1 auto;
      display: flex;
      flex-direction: column;
      min-height: 0;
    }
  `],
})
export class AssistantSidebarComponent {
  protected readonly pageContext = inject(PageContextService);
  protected readonly theme = inject(ThemeService);

  // Resolved scope; null suppresses the entire sidebar (e.g. on /, where the home page's main
  // pane already shows the assistant chat — no point duplicating it).
  protected readonly scope = computed(() => pageContextToScope(this.pageContext.current()));
  protected readonly collapsed = computed(() => this.theme.assistantSidebarCollapsed());

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
