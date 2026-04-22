import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { AuthService } from './auth.service';
import { authConfig } from './auth.config';

/**
 * Redirect anonymous users into the OAuth code flow instead of rendering a half-loaded page
 * that will just fail its XHRs with 401 and surface a confusing error banner. The server
 * remains authoritative — this is UX only.
 */
export const authenticatedGuard: CanActivateFn = (): boolean | UrlTree => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.currentUser()) {
    return true;
  }

  // If OAuth is not configured (dev bypass, local), we can't start the code flow — let the
  // anonymous user through and the API will authenticate them via DevelopmentBypass.
  if (!authConfig.issuer) {
    return true;
  }

  auth.login();
  return router.parseUrl('/');
};
