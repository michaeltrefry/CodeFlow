# CodeFlow UI

Angular 20 SPA for the CodeFlow authoring & operations surface.

## Dev quickstart

```bash
cd codeflow-ui
npm install
npm start            # serves at http://localhost:4200 with proxy to http://localhost:5080
npm test             # runs Angular unit tests with Vitest
npm run test:ci      # single-run unit tests for CI
npm run test:coverage
npm run typecheck
npm run typecheck:spec
```

## Features

- Agent config authoring (type = `agent` or `hitl`, provider + model + prompts, tool allowlist, budget overrides)
- Workflow graph editor (`from → decision → to`, `rotates_round`, escalation agent, max rounds)
- Trace submission and a live monitor driven by SSE (`GET /api/traces/{id}/stream`)
- HITL queue and decision panel (calls `POST /api/traces/{id}/hitl-decision`)

## Auth

Set `window.__cfAuthority` (e.g. from an env-specific `index.html`) to point at the OIDC issuer. In dev the API can run with `Auth:DevelopmentBypass=true` and the SPA treats the `/api/me` response as the current user.

## Unit tests

Unit tests run through the Angular CLI's `@angular/build:unit-test` target with Vitest and jsdom. Put tests next to the code they protect using the `*.spec.ts` suffix.

Prefer tests that lock down meaningful behavior: pure workflow utilities, API/auth contracts, stream parsing, sanitization, form behavior, emitted events, and important rendering branches. Avoid construction-only component tests unless the construction itself proves important Angular wiring.

Use `npm run test:coverage` when checking coverage locally. Coverage is a regression signal, not the goal; a small test that catches a real workflow or auth break is worth more than broad assertions that only execute lines.
