import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { OAuthService } from 'angular-oauth2-oidc';
import { firstValueFrom } from 'rxjs';
import { buildAuthConfig, hasAuthConfigured } from './auth.config';

function withTimeout<T>(promise: Promise<T>, ms: number, message: string): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(new Error(message)), ms);
  });
  return Promise.race([
    promise.finally(() => { if (timer) clearTimeout(timer); }),
    timeout
  ]);
}

export interface CurrentUser {
  id: string;
  email?: string | null;
  name?: string | null;
  roles: string[];
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly oauth = inject(OAuthService);

  readonly currentUser = signal<CurrentUser | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  // Single in-flight bootstrap promise so concurrent callers (AppComponent constructor,
  // route guards) all await the same load instead of triggering duplicate work or seeing
  // an instant-resolve return that misses the still-loading state.
  private loadPromise: Promise<void> | null = null;

  /** Awaitable: resolves once the initial bootstrap (OIDC discovery + /api/me) is complete. */
  ready(): Promise<void> {
    return this.loadPromise ?? this.load();
  }

  load(): Promise<void> {
    if (this.loadPromise) {
      return this.loadPromise;
    }
    this.loadPromise = this.doLoad();
    return this.loadPromise;
  }

  private async doLoad(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);

    try {
      if (hasAuthConfigured()) {
        this.oauth.configure(buildAuthConfig());
        // Time-bound the OIDC discovery + try-login so a hung or misconfigured Keycloak
        // surfaces as a clear error and doesn't lock the app shell forever.
        await withTimeout(
          this.oauth.loadDiscoveryDocumentAndTryLogin(),
          10_000,
          'OIDC discovery/login timed out (>10s). Check the OAUTH_AUTHORITY value and that the Keycloak realm is reachable from the browser.'
        );
      }

      const user = await firstValueFrom(this.http.get<CurrentUser>('/api/me'));
      this.currentUser.set(user);
    } catch (err: unknown) {
      this.currentUser.set(null);
      const message = err instanceof Error ? err.message : 'Unable to load current user.';
      this.error.set(message);
      console.error('[auth] load failed:', err);
    } finally {
      this.loading.set(false);
    }
  }

  getAccessToken(): string | null {
    return this.oauth.getAccessToken() || null;
  }

  login(): void {
    this.oauth.initCodeFlow();
  }

  logout(): void {
    this.oauth.logOut();
    this.currentUser.set(null);
  }
}
