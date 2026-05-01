import { Injectable, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { filter, map, startWith } from 'rxjs/operators';
import type { PageContext } from './page-context';

/**
 * Cross-cutting registry for the current page's {@link PageContext}. Pages with a single,
 * well-known entity (trace detail, workflow editor, agent editor) call {@link set} in their
 * `ngOnInit` and {@link clear} in `ngOnDestroy`. The assistant sidebar (HAA-7) reads
 * {@link current} to scope its conversation; HAA-8 will read it to inject context into prompts.
 *
 * If no page registers a context, {@link current} falls back to a route-derived value: `home`
 * for `/`, `traces-list` for `/traces`, otherwise `{ kind: 'other', route }`. This means pages
 * without a single canonical entity (lists, settings, ops) still get a usable context for free.
 *
 * The page-supplied value wins over the route fallback. Pages MUST clear their registration on
 * destroy so a route change without a new page registering doesn't leave stale state visible.
 */
@Injectable({ providedIn: 'root' })
export class PageContextService {
  private readonly router = inject(Router);
  private readonly registered = signal<PageContext | null>(null);

  // Same router-event pattern AppShellComponent uses for the breadcrumb. A separate signal here
  // keeps the service self-contained — components reading it don't have to also subscribe to
  // router events.
  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map(e => e.urlAfterRedirects),
      startWith(this.router.url),
    ),
    { initialValue: this.router.url },
  );

  readonly current = computed<PageContext>(() => this.registered() ?? this.fromRoute(this.url()));

  set(context: PageContext): void {
    this.registered.set(context);
  }

  clear(): void {
    this.registered.set(null);
  }

  private fromRoute(url: string): PageContext {
    const route = url || '/';
    // Compare on path-only so query params (e.g. `?assistantConversation=…` set by chat-panel
    // when forking or resuming a thread) don't reclassify the homepage as `other` and pop the
    // assistant sidebar back open on top of the home pane's primary chat.
    const path = route.split('?')[0].split('#')[0];
    if (path === '/' || path === '') return { kind: 'home' };
    if (path === '/traces') return { kind: 'traces-list' };
    return { kind: 'other', route };
  }
}
