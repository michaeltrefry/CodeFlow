import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { AuthService } from './auth.service';

/**
 * Keeps guarded pages from rendering before auth bootstrap completes. The public landing page
 * is the only anonymous surface; every other route requires a resolved current user.
 */
export const authenticatedGuard: CanActivateFn = async (): Promise<boolean | UrlTree> => {
  const auth = inject(AuthService);
  const router = inject(Router);

  await auth.ready();

  if (auth.currentUser()) {
    return true;
  }

  // We have a Keycloak token but the API rejected it. Calling login() again would just re-mint
  // the same broken token and loop. Keep the user on the landing page where the auth error can
  // render instead of allowing access to protected routes.
  if (auth.hasToken() && !auth.tokenAcceptedByApi()) {
    return router.parseUrl('/');
  }

  // initCodeFlow() leaves the app for Keycloak; cancel this local navigation.
  auth.login();
  return false;
};
