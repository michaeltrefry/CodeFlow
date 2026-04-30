import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, UrlTree } from '@angular/router';
import { AuthService, type CurrentUser } from './auth.service';
import { authenticatedGuard } from './authenticated.guard';

describe('authenticatedGuard', () => {
  let router: { parseUrl: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    router = { parseUrl: vi.fn((url: string) => ({ url }) as unknown as UrlTree) };
  });

  it('allows navigation after auth bootstrap resolves with a current user', async () => {
    const auth = fakeAuth({
      currentUser: { id: 'user-1', roles: ['Admin'] },
    });
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: auth },
        { provide: Router, useValue: router },
      ],
    });

    const allowed = await runGuard();

    expect(auth.ready).toHaveBeenCalled();
    expect(auth.login).not.toHaveBeenCalled();
    expect(allowed).toBe(true);
  });

  it('starts login and cancels navigation when no user/token is present', async () => {
    const auth = fakeAuth({});
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: auth },
        { provide: Router, useValue: router },
      ],
    });

    const allowed = await runGuard();

    expect(auth.login).toHaveBeenCalled();
    expect(allowed).toBe(false);
  });

  it('redirects to landing without re-login when Keycloak token is rejected by the API', async () => {
    const auth = fakeAuth({
      hasToken: true,
      tokenAcceptedByApi: false,
    });
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: auth },
        { provide: Router, useValue: router },
      ],
    });

    const allowed = await runGuard();

    expect(auth.login).not.toHaveBeenCalled();
    expect(router.parseUrl).toHaveBeenCalledWith('/');
    expect(allowed).toEqual({ url: '/' });
  });
});

async function runGuard(): Promise<boolean | UrlTree> {
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
