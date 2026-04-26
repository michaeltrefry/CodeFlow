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
  /**
   * True after a successful OIDC token exchange. Distinct from `currentUser`:
   * if we hold a valid bearer but `/api/me` 401s, that means the API rejected our token
   * (wrong aud, wrong issuer, signature mismatch). The guard MUST NOT redirect to Keycloak
   * again in that case — it would just re-mint the same broken token in a loop.
   */
  readonly hasToken = signal(false);
  /** True iff we have a token AND the API accepted it AND `/api/me` came back 200. */
  readonly tokenAcceptedByApi = signal(false);

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
    this.hasToken.set(false);
    this.tokenAcceptedByApi.set(false);

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
        this.hasToken.set(this.oauth.hasValidAccessToken());
      }

      try {
        const user = await firstValueFrom(this.http.get<CurrentUser>('/api/me'));
        this.currentUser.set(user);
        this.tokenAcceptedByApi.set(true);
      } catch (apiErr: unknown) {
        this.currentUser.set(null);
        if (this.hasToken()) {
          // We have a Keycloak-issued bearer, but /api/me rejected it. This is almost always
          // an audience or issuer mismatch on the API side (wrong Auth__Audience / Auth__Authority,
          // or the Keycloak audience mapper isn't surfacing `codeflow-api` in `aud`). Surface a
          // clear error — the guard will see `hasToken && !tokenAcceptedByApi` and stop trying
          // to log the user in again.
          this.error.set(
            'Signed in to Keycloak, but the CodeFlow API rejected the access token. ' +
            'Most likely cause: the access token does not contain `codeflow-api` in the `aud` claim. ' +
            'Check the Audience mapper on the codeflow-ui client and the API\'s Auth__Authority/Auth__Audience env vars.'
          );
          console.error('[auth] /api/me rejected a valid Keycloak token:', apiErr);
        } else {
          // No token AND no /api/me access: anonymous. Guard will redirect to Keycloak.
          throw apiErr;
        }
      }
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
