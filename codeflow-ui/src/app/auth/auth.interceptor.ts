import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { AuthService } from './auth.service';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const token = auth.getAccessToken();

  if (!token) {
    return next(req);
  }

  // Only attach the bearer to same-origin requests; otherwise any future HttpClient call to an
  // external service would leak the access token off-origin.
  if (!isSameOrigin(req.url)) {
    return next(req);
  }

  return next(req.clone({
    setHeaders: {
      Authorization: `Bearer ${token}`
    }
  }));
};

function isSameOrigin(url: string): boolean {
  if (!url) {
    return true;
  }
  // Relative URLs resolve against the current origin.
  if (url.startsWith('/') || url.startsWith('./') || url.startsWith('../')) {
    return true;
  }
  try {
    const origin = typeof window !== 'undefined' && window.location
      ? window.location.origin
      : '';
    return origin.length > 0 && url.startsWith(origin + '/');
  } catch {
    return false;
  }
}
