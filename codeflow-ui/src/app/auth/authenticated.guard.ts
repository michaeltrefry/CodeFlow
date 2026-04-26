import { inject } from '@angular/core';
import { CanActivateFn } from '@angular/router';
import { AuthService } from './auth.service';
import { hasAuthConfigured } from './auth.config';

/**
 * Redirect anonymous users into the OAuth code flow instead of rendering a half-loaded page
 * that will just fail its XHRs with 401 and surface a confusing error banner. The server
 * remains authoritative — this is UX only.
 *
 * Implementation notes:
 * - We MUST await `auth.ready()` before deciding. The router fires this guard during initial
 *   navigation, before AppComponent's `auth.load()` has resolved. Without this wait, the guard
 *   sees `currentUser()===null` even for a logged-in user and incorrectly bounces to Keycloak.
 * - We MUST NOT redirect to '/' (or any other guarded path) on the unauthenticated branch:
 *   '/' redirects to '/traces' which has the same guard, which would re-enter and lock up the
 *   router in a synchronous loop until Chrome shows "Page Unresponsive".
 * - `auth.login()` triggers `initCodeFlow()` which navigates the window to Keycloak; returning
 *   `false` cancels the in-progress navigation so nothing else happens locally.
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

  auth.login();
  return false;
};
