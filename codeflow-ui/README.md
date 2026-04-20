# CodeFlow UI

Angular 20 SPA for the CodeFlow authoring & operations surface.

## Dev quickstart

```bash
cd codeflow-ui
npm install
npm start            # serves at http://localhost:4200 with proxy to http://localhost:5080
```

## Features

- Agent config authoring (type = `agent` or `hitl`, provider + model + prompts, tool allowlist, budget overrides)
- Workflow graph editor (`from → decision → to`, `rotates_round`, escalation agent, max rounds)
- Trace submission and a live monitor driven by SSE (`GET /api/traces/{id}/stream`)
- HITL queue and decision panel (calls `POST /api/traces/{id}/hitl-decision`)

## Auth

Set `window.__cfAuthority` (e.g. from an env-specific `index.html`) to point at the OIDC issuer. In dev the API can run with `Auth:DevelopmentBypass=true` and the SPA treats the `/api/me` response as the current user.
