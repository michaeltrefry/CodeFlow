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
npm run coverage:check
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

Use `npm run test:ci` for a normal local or CI pass. Use `npm run typecheck:spec` when a test adds typed fakes, TestBed providers, HTTP testing, or helper DTOs.

Use `npm run coverage:check` before raising coverage-sensitive frontend PRs. Coverage is a regression signal, not the goal; a small test that catches a real workflow or auth break is worth more than broad assertions that only execute lines. The initial global thresholds are intentionally low so contributors do not write shallow tests to satisfy a percentage, but the command will catch accidental removal of the current coverage floor.

### What To Test

- Test pure helpers directly: serialization, diffing, runtime-config merging, autocomplete suggestions, stream parsing, markdown rendering, and package summaries.
- Test API wrappers with `HttpTestingController`: URL encoding, HTTP method, query params, request body, response type, and `observe` options.
- Test auth behavior through public service/guard/interceptor seams: bootstrap state, token refresh, redirect decisions, API-rejected-token handling, and same-origin `/api` token attachment.
- Test components when the assertion protects behavior a user depends on: submitted forms, emitted events, disabled/loading states, conditional branches, routing decisions, and meaningful rendered text.
- Keep Monaco, Rete, and other third-party-heavy surfaces behind focused utility, adapter, or component-shell tests instead of brittle DOM simulations.

### Test Shape

- Prefer one behavior per test and name the behavior in the `it(...)` text.
- Keep fixtures small and domain-realistic: workflow keys, agent pins, trace ids, provider/model names, and HITL decisions should look like CodeFlow data.
- Mock browser/network seams at the boundary. Use `HttpTestingController` for Angular `HttpClient` wrappers and small `ReadableStream` fixtures for fetch-based SSE helpers.
- Avoid tests that only assert a component can be created unless creation proves important Angular wiring or dependency configuration.
- Avoid snapshots for dynamic UI. Assert the contract: text, state, emitted values, HTTP request shape, or sanitized HTML.

### Coverage Guardrail

`npm run coverage:check` runs coverage and then checks `coverage-summary.json` with `scripts/check-coverage-thresholds.mjs`.

Current global minimums:

- Statements: 10%
- Branches: 5%
- Functions: 10%
- Lines: 10%

Raise these only after coverage improves for meaningful behavior. Do not raise thresholds by adding tests whose only purpose is to execute lines.
