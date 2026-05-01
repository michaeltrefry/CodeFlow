import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { filter, map, startWith } from 'rxjs/operators';
import { AuthService } from '../auth/auth.service';
import { ThemeService } from '../core/theme.service';
import { LayoutService } from '../core/layout.service';
import { IconComponent, IconName } from '../ui/icon.component';
import { ChipComponent } from '../ui/chip.component';
import { TweaksPanelComponent } from './tweaks-panel.component';
import { AssistantSidebarComponent } from './assistant-sidebar.component';

interface NavItem {
  id: string;
  label: string;
  icon: IconName;
  route: string;
  badge?: string;
}

interface NavGroup {
  label?: string;
  items: NavItem[];
}

const NAV_GROUPS: NavGroup[] = [
  {
    items: [
      { id: 'home',      label: 'Home',       icon: 'bot',       route: '/' },
      { id: 'traces',    label: 'Traces',     icon: 'traces',    route: '/traces' },
      { id: 'workflows', label: 'Workflows',  icon: 'workflows', route: '/workflows' },
      { id: 'agents',    label: 'Agents',     icon: 'agents',    route: '/agents' },
      { id: 'hitl',      label: 'HITL queue', icon: 'hitl',      route: '/hitl' },
      { id: 'dlq',       label: 'DLQ ops',    icon: 'dlq',       route: '/ops/dlq' },
    ],
  },
  {
    label: 'Settings',
    items: [
      { id: 'mcp',    label: 'MCP servers',   icon: 'mcp',    route: '/settings/mcp-servers' },
      { id: 'roles',  label: 'Roles',         icon: 'roles',  route: '/settings/roles' },
      { id: 'skills', label: 'Skills',        icon: 'skills', route: '/settings/skills' },
      { id: 'git',    label: 'Git host',      icon: 'git',    route: '/settings/git-host' },
      { id: 'llm',    label: 'LLM providers', icon: 'bot',    route: '/settings/llm-providers' },
      { id: 'notify', label: 'Notifications', icon: 'bot',    route: '/settings/notifications' },
    ],
  },
];

const TITLE_FOR_ROUTE: Array<{ match: (url: string) => boolean; title: string }> = [
  { match: (u) => u === '/' || u === '',           title: 'Home' },
  { match: (u) => u.startsWith('/traces/new'),     title: 'New trace' },
  { match: (u) => /^\/traces\//.test(u),           title: 'Trace' },
  { match: (u) => u === '/traces',                 title: 'Traces' },
  { match: (u) => u === '/workflows/new',          title: 'New workflow' },
  { match: (u) => /^\/workflows\//.test(u),        title: 'Workflow' },
  { match: (u) => u === '/workflows',              title: 'Workflows' },
  { match: (u) => u === '/agents/new',             title: 'New agent' },
  { match: (u) => /^\/agents\/[^/]+\/edit/.test(u),title: 'Edit agent' },
  { match: (u) => /^\/agents\/[^/]+\/test/.test(u),title: 'Test agent' },
  { match: (u) => /^\/agents\//.test(u),           title: 'Agent' },
  { match: (u) => u === '/agents',                 title: 'Agents' },
  { match: (u) => u === '/hitl',                   title: 'HITL queue' },
  { match: (u) => u === '/ops/dlq',                title: 'DLQ ops' },
  { match: (u) => u.startsWith('/settings/mcp'),   title: 'MCP servers' },
  { match: (u) => u.startsWith('/settings/roles'), title: 'Roles' },
  { match: (u) => u.startsWith('/settings/skills'),title: 'Skills' },
  { match: (u) => u.startsWith('/settings/git'),   title: 'Git host' },
  { match: (u) => u.startsWith('/settings/llm-providers'), title: 'LLM providers' },
];

@Component({
  selector: 'cf-app-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, IconComponent, ChipComponent, TweaksPanelComponent, AssistantSidebarComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (publicSurface()) {
      <router-outlet />
    } @else {
      <div class="shell" [attr.data-nav]="theme.navCollapsed() ? 'collapsed' : 'expanded'">
      <aside class="nav">
        <div class="nav-brand">
          <cf-icon class="brand-mark" name="codeflowApp" [sizeOverride]="22"></cf-icon>
          <span class="brand-wordmark">CodeFlow</span>
        </div>
        <div class="nav-links">
          @for (g of navGroups; track $index) {
            @if (g.label) {
              <div class="nav-section-label">{{ g.label }}</div>
            } @else {
              <div class="nav-section-spacer"></div>
            }
            @for (it of g.items; track it.id) {
              <a class="nav-link"
                 [routerLink]="it.route"
                 routerLinkActive="is-active"
                 #rla="routerLinkActive"
                 [attr.data-active]="rla.isActive ? 'true' : null"
                 [title]="it.label">
                <cf-icon [name]="it.icon"></cf-icon>
                <span class="nav-link-label">{{ it.label }}</span>
                @if (it.badge) {
                  <span class="nav-link-badge">{{ it.badge }}</span>
                }
              </a>
            }
          }
        </div>
        <div class="nav-footer">
          <button type="button" class="nav-toggle" (click)="theme.toggleNav()" title="Toggle nav">
            <cf-icon name="panelL"></cf-icon>
            <span class="nav-toggle-label">{{ theme.navCollapsed() ? 'Expand' : 'Collapse' }}</span>
          </button>
          <div class="nav-user">
            <div class="nav-user-avatar">{{ initials() }}</div>
            <div class="nav-user-body">
              <div class="nav-user-name">{{ userName() }}</div>
              <div class="nav-user-roles">
                @for (role of roles(); track role) {
                  <cf-chip variant="accent">{{ role }}</cf-chip>
                }
                @if (!roles().length && !auth.currentUser()) {
                  <cf-chip>guest</cf-chip>
                }
              </div>
            </div>
          </div>
        </div>
      </aside>

      <div class="workspace">
        <div class="topbar">
          <div class="breadcrumb">
            <span>CodeFlow</span>
            <span class="sep">/</span>
            <span class="current">{{ currentTitle() }}</span>
            @if (layout.subcrumb()) {
              <span class="sep">/</span>
              <span class="mono" style="color: var(--muted)">{{ layout.subcrumb() }}</span>
            }
          </div>
          <button type="button" class="topbar-icon-btn" (click)="toggleTweaks()" title="Tweaks">
            <cf-icon name="settings"></cf-icon>
          </button>
        </div>

        <div class="workspace-body">
          <div class="workspace-content">
            <router-outlet />
          </div>
          <cf-assistant-sidebar />
        </div>
      </div>
      </div>
    }

    @if (tweaksOpen()) {
      <cf-tweaks-panel (closeRequest)="tweaksOpen.set(false)"></cf-tweaks-panel>
    }
  `,
})
export class AppShellComponent {
  readonly auth = inject(AuthService);
  readonly theme = inject(ThemeService);
  readonly layout = inject(LayoutService);
  private readonly router = inject(Router);

  readonly navGroups = NAV_GROUPS;
  readonly tweaksOpen = signal(false);

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map(e => e.urlAfterRedirects),
      startWith(this.router.url),
    ),
    { initialValue: this.router.url },
  );

  readonly currentTitle = computed(() => {
    const u = this.url() || '/';
    const match = TITLE_FOR_ROUTE.find(x => x.match(u));
    return match?.title ?? 'Home';
  });

  readonly publicSurface = computed(() => !this.auth.loading() && this.auth.currentUser() === null);

  readonly userName = computed(() => {
    const u = this.auth.currentUser();
    return u?.name ?? u?.email ?? u?.id ?? 'Not signed in';
  });

  readonly initials = computed(() => {
    const name = this.userName();
    if (!name || name === 'Not signed in') return '?';
    return name.split(/\s+/).map(p => p[0]).filter(Boolean).slice(0, 2).join('').toUpperCase() || name.slice(0, 2).toUpperCase();
  });

  readonly roles = computed(() => this.auth.currentUser()?.roles ?? []);

  toggleTweaks(): void { this.tweaksOpen.update(v => !v); }
}
