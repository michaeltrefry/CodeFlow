import { DOCUMENT } from '@angular/common';
import { Injectable, computed, effect, inject, signal } from '@angular/core';

export type ThemeMode = 'dark' | 'light';
export type AccentName = 'indigo' | 'cyan' | 'green' | 'amber';
export type FontName = 'geist' | 'inter' | 'plex';
export type AssistantSidebarMode = 'collapsed' | 'docked' | 'expanded';

const STORAGE_KEY = 'cf.ui.tweaks';

interface TweakState {
  theme: ThemeMode;
  accent: AccentName;
  font: FontName;
  navCollapsed: boolean;
  assistantSidebarMode: AssistantSidebarMode;
  assistantSidebarCollapsed: boolean;
}

const DEFAULTS: TweakState = {
  theme: 'dark',
  accent: 'indigo',
  font: 'plex',
  navCollapsed: false,
  // Default open: HAA-7 wants the assistant discoverable on first visit. The user's collapse
  // choice persists thereafter via localStorage.
  assistantSidebarMode: 'docked',
  assistantSidebarCollapsed: false,
};

const THEMES: ReadonlyArray<ThemeMode> = ['dark', 'light'];
const ACCENTS: ReadonlyArray<AccentName> = ['indigo', 'cyan', 'green', 'amber'];
const FONTS: ReadonlyArray<FontName> = ['geist', 'inter', 'plex'];
const ASSISTANT_SIDEBAR_MODES: ReadonlyArray<AssistantSidebarMode> = ['collapsed', 'docked', 'expanded'];

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly doc = inject(DOCUMENT);

  readonly theme = signal<ThemeMode>(DEFAULTS.theme);
  readonly accent = signal<AccentName>(DEFAULTS.accent);
  readonly font = signal<FontName>(DEFAULTS.font);
  readonly navCollapsed = signal<boolean>(DEFAULTS.navCollapsed);
  readonly assistantSidebarMode = signal<AssistantSidebarMode>(DEFAULTS.assistantSidebarMode);
  readonly assistantSidebarCollapsed = computed<boolean>(() => this.assistantSidebarMode() === 'collapsed');

  readonly snapshot = computed<TweakState>(() => ({
    theme: this.theme(),
    accent: this.accent(),
    font: this.font(),
    navCollapsed: this.navCollapsed(),
    assistantSidebarMode: this.assistantSidebarMode(),
    assistantSidebarCollapsed: this.assistantSidebarCollapsed(),
  }));

  constructor() {
    this.hydrate();

    effect(() => {
      const root = this.doc.documentElement;
      const { theme, accent, font } = this.snapshot();
      root.setAttribute('data-theme', theme);
      root.setAttribute('data-accent', accent);
      root.setAttribute('data-font', font);
    });

    effect(() => {
      const state = this.snapshot();
      try {
        this.doc.defaultView?.localStorage?.setItem(STORAGE_KEY, JSON.stringify(state));
      } catch {
        // Ignore quota / privacy-mode failures; tweaks revert to defaults next load.
      }
    });
  }

  setTheme(theme: ThemeMode): void { this.theme.set(theme); }
  setAccent(accent: AccentName): void { this.accent.set(accent); }
  setFont(font: FontName): void { this.font.set(font); }
  toggleNav(): void { this.navCollapsed.update(v => !v); }
  setNavCollapsed(collapsed: boolean): void { this.navCollapsed.set(collapsed); }
  toggleAssistantSidebar(): void {
    this.assistantSidebarMode.update(mode => mode === 'collapsed' ? 'docked' : 'collapsed');
  }
  setAssistantSidebarCollapsed(collapsed: boolean): void {
    this.assistantSidebarMode.set(collapsed ? 'collapsed' : 'docked');
  }
  setAssistantSidebarMode(mode: AssistantSidebarMode): void {
    this.assistantSidebarMode.set(mode);
  }

  private hydrate(): void {
    const raw = (() => {
      try { return this.doc.defaultView?.localStorage?.getItem(STORAGE_KEY) ?? null; }
      catch { return null; }
    })();
    if (!raw) return;
    try {
      const parsed = JSON.parse(raw) as Partial<TweakState>;
      if (parsed.theme && THEMES.includes(parsed.theme)) this.theme.set(parsed.theme);
      if (parsed.accent && ACCENTS.includes(parsed.accent)) this.accent.set(parsed.accent);
      if (parsed.font && FONTS.includes(parsed.font)) this.font.set(parsed.font);
      if (typeof parsed.navCollapsed === 'boolean') this.navCollapsed.set(parsed.navCollapsed);
      if (parsed.assistantSidebarMode && ASSISTANT_SIDEBAR_MODES.includes(parsed.assistantSidebarMode)) {
        this.assistantSidebarMode.set(parsed.assistantSidebarMode);
      } else if (typeof parsed.assistantSidebarCollapsed === 'boolean') {
        this.setAssistantSidebarCollapsed(parsed.assistantSidebarCollapsed);
      }
    } catch {
      // Ignore corrupt storage; fall through to defaults.
    }
  }
}
