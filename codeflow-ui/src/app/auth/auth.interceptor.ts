import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { from, switchMap } from 'rxjs';
import { AuthService } from './auth.service';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);

  // Only attach the bearer to CodeFlow API requests; otherwise OAuth/token endpoint calls or
  // future external HttpClient calls could either deadlock refresh or leak the access token.
  if (!isApiRequest(req.url)) {
    return next(req);
  }

  return from(auth.getValidAccessToken()).pipe(
    switchMap(token => {
      if (!token) {
        return next(req);
      }

      return next(req.clone({
        setHeaders: {
          Authorization: `Bearer ${token}`
        }
      }));
    })
  );
};

function isApiRequest(url: string): boolean {
  if (!url) {
    return false;
  }
  // Relative URLs resolve against the current origin.
  if (url === '/api' || url.startsWith('/api/')) {
    return true;
  }
  try {
    const base = typeof window !== 'undefined' && window.location
      ? window.location.origin
      : 'http://localhost';
    const parsed = new URL(url, base);
    const currentOrigin = typeof window !== 'undefined' && window.location
      ? window.location.origin
      : parsed.origin;
    return parsed.origin === currentOrigin && (parsed.pathname === '/api' || parsed.pathname.startsWith('/api/'));
  } catch {
    return false;
  }
}
