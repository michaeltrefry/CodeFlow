import { inject } from '@angular/core';
import { CanActivateFn } from '@angular/router';
import { AuthService } from './auth.service';
import { hasAuthConfigured } from './auth.config';

/**
 * Keeps guarded pages from rendering before auth bootstrap completes, then sends anonymous
 * users into the OAuth code flow. The server remains authoritative; this is UX only.
 */
export const authenticatedGuard: CanActivateFn = async (): Promise<boolean> => {
  const auth = inject(AuthService);

  await auth.ready();

  if (auth.currentUser()) {
    return true;
  }

  // If OAuth is not configured (dev bypass, local), we can't start the code flow — let the
  // anonymous user through and the API will authenticate them via DevelopmentBypass.
  if (!hasAuthConfigured()) {
    return true;
  }

  // We have a Keycloak token but the API rejected it. Calling login() again would just re-mint
  // the same broken token and loop. Let the user through so the AppShell can render the auth
  // error instead of trapping them in a redirect loop.
  if (auth.hasToken() && !auth.tokenAcceptedByApi()) {
    return true;
  }

  // initCodeFlow() leaves the app for Keycloak; cancel this local navigation.
  auth.login();
  return false;
};
