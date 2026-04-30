import type { Route } from '@angular/router';
import { authenticatedGuard } from './auth/authenticated.guard';
import { routes } from './app.routes';

describe('app routes', () => {
  it('guards protected agent routes', () => {
    expectProtectedRoutesGuarded(['agents', 'agents/new', 'agents/:key/test', 'agents/:key/edit', 'agents/:key']);
  });
});

function expectProtectedRoutesGuarded(paths: string[]): void {
  for (const path of paths) {
    const route = findRoute(path);

    expect(route?.canActivate).toContain(authenticatedGuard);
  }
}

function findRoute(path: string): Route | undefined {
  return routes.find(route => route.path === path);
}
