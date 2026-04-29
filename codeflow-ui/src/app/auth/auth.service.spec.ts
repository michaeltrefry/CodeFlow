import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { OAuthService } from 'angular-oauth2-oidc';
import { AuthService, type CurrentUser } from './auth.service';
import { loadRuntimeConfig } from '../core/runtime-config';

describe('AuthService', () => {
  let httpMock: HttpTestingController;
  let oauth: FakeOAuthService;

  beforeEach(async () => {
    await setRuntimeAuthConfigured(true);
    oauth = new FakeOAuthService();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: OAuthService, useValue: oauth },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('loads OIDC config, fetches /api/me, and marks the token accepted by the API', async () => {
    oauth.validAccessToken = true;
    oauth.accessToken = 'bearer';
    const auth = TestBed.inject(AuthService);

    const loadPromise = auth.load();
    const req = await expectOneEventually(httpMock, '/api/me');
    expect(req.request.method).toBe('GET');
    req.flush(currentUser());
    await loadPromise;

    expect(oauth.configure).toHaveBeenCalledWith(expect.objectContaining({
      issuer: 'https://id.example.test/realms/codeflow',
      clientId: 'codeflow-ui',
    }));
    expect(oauth.setupAutomaticSilentRefresh).toHaveBeenCalled();
    expect(oauth.loadDiscoveryDocumentAndTryLogin).toHaveBeenCalled();
    expect(auth.currentUser()).toEqual(currentUser());
    expect(auth.hasToken()).toBe(true);
    expect(auth.tokenAcceptedByApi()).toBe(true);
    expect(auth.error()).toBeNull();
    expect(auth.loading()).toBe(false);
  });

  it('surfaces a clear API-rejected-token error without clearing the Keycloak token flag', async () => {
    oauth.validAccessToken = true;
    oauth.accessToken = 'bearer';
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const auth = TestBed.inject(AuthService);

    const loadPromise = auth.load();
    const req = await expectOneEventually(httpMock, '/api/me');
    req.flush({ message: 'Unauthorized' }, {
      status: 401,
      statusText: 'Unauthorized',
    });
    await loadPromise;

    expect(auth.currentUser()).toBeNull();
    expect(auth.hasToken()).toBe(true);
    expect(auth.tokenAcceptedByApi()).toBe(false);
    expect(auth.error()).toContain('CodeFlow API rejected the access token');
  });

  it('refreshes an expired access token once and returns the refreshed bearer', async () => {
    const auth = TestBed.inject(AuthService);
    oauth.validAccessToken = false;
    oauth.refreshTokenValue = 'refresh-token';
    oauth.refreshToken.mockImplementation(async () => {
      oauth.validAccessToken = true;
      oauth.accessToken = 'refreshed-token';
    });

    const token = await auth.getValidAccessToken();

    expect(oauth.refreshToken).toHaveBeenCalledTimes(1);
    expect(token).toBe('refreshed-token');
    expect(auth.hasToken()).toBe(true);
    expect(auth.error()).toBeNull();
  });

  it('marks auth state unavailable when refresh fails', async () => {
    const auth = TestBed.inject(AuthService);
    oauth.validAccessToken = false;
    oauth.refreshTokenValue = 'refresh-token';
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    oauth.refreshToken.mockRejectedValue(new Error('refresh failed'));

    const token = await auth.getValidAccessToken();

    expect(token).toBeNull();
    expect(auth.hasToken()).toBe(false);
    expect(auth.tokenAcceptedByApi()).toBe(false);
    expect(auth.currentUser()).toBeNull();
  });
});

class FakeOAuthService {
  validAccessToken = false;
  accessToken = '';
  refreshTokenValue: string | null = null;
  configure = vi.fn();
  setupAutomaticSilentRefresh = vi.fn();
  loadDiscoveryDocumentAndTryLogin = vi.fn().mockResolvedValue(true);
  hasValidAccessToken = vi.fn(() => this.validAccessToken);
  getAccessToken = vi.fn(() => this.accessToken);
  getRefreshToken = vi.fn(() => this.refreshTokenValue);
  refreshToken = vi.fn<() => Promise<void>>().mockResolvedValue(undefined);
  initCodeFlow = vi.fn();
  logOut = vi.fn();
}

async function setRuntimeAuthConfigured(configured: boolean): Promise<void> {
  vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue({
    ok: true,
    status: 200,
    json: vi.fn().mockResolvedValue({
      oauth: {
        authority: configured ? 'https://id.example.test/realms/codeflow' : '',
      },
    }),
  } as unknown as Response));
  await loadRuntimeConfig();
}

function currentUser(): CurrentUser {
  return {
    id: 'user-1',
    email: 'user@example.test',
    name: 'Test User',
    roles: ['Admin'],
  };
}

async function expectOneEventually(httpMock: HttpTestingController, url: string) {
  for (let i = 0; i < 10; i++) {
    const matches = httpMock.match(url);
    if (matches.length === 1) {
      return matches[0];
    }
    await Promise.resolve();
  }
  return httpMock.expectOne(url);
}
