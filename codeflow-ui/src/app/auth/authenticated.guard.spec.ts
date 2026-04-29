import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AuthService, type CurrentUser } from './auth.service';
import { authenticatedGuard } from './authenticated.guard';
import { loadRuntimeConfig } from '../core/runtime-config';

describe('authenticatedGuard', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('allows navigation after auth bootstrap resolves with a current user', async () => {
    await setAuthConfigured(true);
    const auth = fakeAuth({
      currentUser: { id: 'user-1', roles: ['Admin'] },
    });
    TestBed.configureTestingModule({
      providers: [{ provide: AuthService, useValue: auth }],
    });

    const allowed = await runGuard();

    expect(auth.ready).toHaveBeenCalled();
    expect(auth.login).not.toHaveBeenCalled();
    expect(allowed).toBe(true);
  });

  it('allows anonymous navigation when OAuth is not configured for local/dev bypass', async () => {
    await setAuthConfigured(false);
    const auth = fakeAuth({});
    TestBed.configureTestingModule({
      providers: [{ provide: AuthService, useValue: auth }],
    });

    const allowed = await runGuard();

    expect(auth.login).not.toHaveBeenCalled();
    expect(allowed).toBe(true);
  });

  it('allows the shell to render auth errors when Keycloak token is rejected by the API', async () => {
    await setAuthConfigured(true);
    const auth = fakeAuth({
      hasToken: true,
      tokenAcceptedByApi: false,
    });
    TestBed.configureTestingModule({
      providers: [{ provide: AuthService, useValue: auth }],
    });

    const allowed = await runGuard();

    expect(auth.login).not.toHaveBeenCalled();
    expect(allowed).toBe(true);
  });

  it('starts login and cancels navigation when OAuth is configured and no user/token is present', async () => {
    await setAuthConfigured(true);
    const auth = fakeAuth({});
    TestBed.configureTestingModule({
      providers: [{ provide: AuthService, useValue: auth }],
    });

    const allowed = await runGuard();

    expect(auth.login).toHaveBeenCalled();
    expect(allowed).toBe(false);
  });
});

async function runGuard(): Promise<boolean> {
  return TestBed.runInInjectionContext(() =>
    authenticatedGuard({} as never, {} as never) as Promise<boolean>
  );
}

function fakeAuth(options: {
  currentUser?: CurrentUser | null;
  hasToken?: boolean;
  tokenAcceptedByApi?: boolean;
}) {
  return {
    ready: vi.fn().mockResolvedValue(undefined),
    currentUser: signal(options.currentUser ?? null),
    hasToken: signal(options.hasToken ?? false),
    tokenAcceptedByApi: signal(options.tokenAcceptedByApi ?? false),
    login: vi.fn(),
  };
}

async function setAuthConfigured(configured: boolean): Promise<void> {
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
