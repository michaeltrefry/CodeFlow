# Prompt partials (P1 / F3)

Reusable Scriban template fragments under the `@codeflow/` scope, included via `{{ include "@codeflow/<name>" }}` and pinned per-agent for stability.

## Stock partials (v1)

| Key | Use on | What it provides |
|---|---|---|
| `@codeflow/reviewer-base` | Reviewer agents inside bounded loops | Approval bias; "no default to Rejected"; "no iteration target" — counters the model's natural rejection bias. |
| `@codeflow/producer-base` | Producer agents inside loops (architects, devs, writers) | Non-negotiable-feedback language; forbids metadata sections like `## Changes Made`; reinforces that the message body IS the artifact. |
| `@codeflow/last-round-reminder` | Auto-injected into ReviewLoop children (P2) | `{{ if isLastRound }}` block telling the model the round budget is exhausting. Opt out per-node via `optOutLastRoundReminder: true`. |
| `@codeflow/no-metadata-sections` | Any artifact-producing agent | Forbids "## Changes Made", "## Diff", inline rationale. |
| `@codeflow/write-before-submit` | Any agent submitting on non-sentinel ports | Reminder that the message body IS the artifact (not the submit payload). |

## Configuration

On the agent's config:

```json
"partialPins": [
  { "key": "@codeflow/reviewer-base", "version": 1 }
]
```

Then in the system prompt:

```
You are a senior reviewer of implementation plans.

{{ include "@codeflow/reviewer-base" }}

## What you are checking
1. Gaps...
```

The `{{ include }}` directive resolves at render time using the agent's pinned partial bodies (no DB roundtrip). If the pinned partial is missing, the renderer surfaces the include name in a `PromptTemplateException`.

## Pinning model

Partials are versioned and immutable per `(key, version)`. Bumping a partial is a deliberate platform-release action — the platform ships v2 of `@codeflow/reviewer-base`, the seeder inserts the new row, but agents that pin v1 keep rendering against v1. Authors who want the v2 wording bump their agent and update `partialPins` to `version: 2`.

The cascade-bump assistant (E4) walks the dependency tree for partial bumps too — bumping a partial cascades to every agent that pins it.

## Custom partials

Use the partials editor (currently API-only via `POST /api/prompt-partials`) to add partials at custom keys. Conventional choices:
- `@<your-org>/<name>` for org-internal partials.
- `@<workflow>/<name>` for workflow-specific partials.

Partials at non-`@codeflow/` keys are user-managed; the platform doesn't seed them.

## Auto-injection (P2)

Inside ReviewLoop child sagas, the framework automatically appends `@codeflow/last-round-reminder` to the agent's prompt template — without the author having to include it. Opt out per-node via `optOutLastRoundReminder: true` on the workflow node.

The injection is visible in the rendered template via a Scriban-comment delimiter (`{{# [auto-injected] @codeflow/last-round-reminder #}}`) so the future live-prompt-preview UI (VZ3) can annotate it.

## See also

- `CodeFlow.Persistence/SystemPromptPartials.cs` — source of the v1 stock catalog.
- `CodeFlow.Persistence/SystemPromptPartialSeeder.cs` — runs on host startup.
