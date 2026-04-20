import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { OAuthService } from 'angular-oauth2-oidc';
import { firstValueFrom } from 'rxjs';
import { authConfig } from './auth.config';

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

  async load(): Promise<void> {
    if (this.loading()) {
      return;
    }

    this.loading.set(true);
    this.error.set(null);

    try {
      if (authConfig.issuer) {
        this.oauth.configure(authConfig);
        await this.oauth.loadDiscoveryDocumentAndTryLogin();
      }

      const user = await firstValueFrom(this.http.get<CurrentUser>('/api/me'));
      this.currentUser.set(user);
    } catch (err: unknown) {
      this.currentUser.set(null);
      this.error.set(err instanceof Error ? err.message : 'Unable to load current user.');
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
