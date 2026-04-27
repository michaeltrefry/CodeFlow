# Workflow templates (S3 framework + S4-S7 templates)

The "New from Template" picker on the Workflows page collapses 30 minutes of wiring into 30 seconds. Templates ship with the platform; authors pick one, give it a name prefix, and get a fully-wired workflow + agents materialized at v1.

## Available templates

### Empty workflow (`empty-workflow`)

A single Start node + placeholder agent. Materializes:
- `<prefix>-start` (Agent v1) — placeholder system prompt.
- `<prefix>` (Workflow v1) — Start node wired to the agent.

Use when you want full structural control without scaffolding decisions.

### HITL approval gate (`hitl-approval-gate`)

A standalone workflow with one human-in-the-loop checkpoint. Materializes:
- `<prefix>-trigger` (Agent v1) — minimal kickoff that forwards the workflow input verbatim into the HITL form.
- `<prefix>-form` (Hitl agent v1) — passthrough `outputTemplate: "{{ input }}"`; declares `Approved` + `Cancelled` ports.
- `<prefix>` (Workflow v1) — Start (trigger) → Hitl (form).

Drop the resulting workflow as a Subflow node anywhere you want a human checkpoint between subflows. The Subflow node automatically inherits the Approved + Cancelled ports.

### Lifecycle wrapper (`lifecycle-wrapper`)

A multi-phase shell — chained subflows with HITL gates between them. Codifies the shape of `lifecycle-v1` (PRD intake → impl-plan → dev-flow → publish). Default 3 phases:
- `<prefix>-trigger` (Agent v1) — kickoff that forwards input to phase 1.
- `<prefix>-phase-trigger` (Agent v1) — shared placeholder agent each phase stub uses.
- `<prefix>-phase-1` / `-phase-2` / `-phase-3` (Workflow v1 each) — single-node stubs the author replaces (or repoints) with real phase workflows.
- `<prefix>-gate-1-form` / `-gate-2-form` (Hitl agent v1 each) — passthrough approval gates between phases with `Approved` + `Cancelled` ports.
- `<prefix>` (Workflow v1) — the lifecycle: Start (trigger) → Subflow (phase-1) → Hitl (gate-1) → Subflow (phase-2) → Hitl (gate-2) → Subflow (phase-3) → terminal.

Author replaces each `<prefix>-phase-N` workflow's content (or repoints the lifecycle's Subflow node to point at a different workflow) with the real phase. Phase count is fixed at 3 in this materializer; authors who want more phases edit the outer workflow post-materialization (add Subflow + Hitl pairs and rewire).

### Setup → loop → finalize (`setup-loop-finalize`)

The doc's "setup agent before a loop" pattern. A setup agent seeds the workflow bag from the input, then a producer/reviewer ReviewLoop iterates against that bag, with an HITL escalation on the Exhausted port. Materializes 6 entities:
- `<prefix>-setup` (Agent v1) — Start setup agent. The workflow node carries an `inputScript` with a TODO comment block showing where to seed `workflow.*` from `input.*`.
- `<prefix>-producer` (Agent v1) — pins `@codeflow/producer-base`.
- `<prefix>-reviewer` (Agent v1) — pins `@codeflow/reviewer-base`; declares `Approved` + `Rejected`.
- `<prefix>-escalation-form` (Hitl agent v1) — passthrough form with `Approved` + `Cancelled` ports the operator chooses on round-budget exhaustion.
- `<prefix>-inner` (Workflow v1) — Start (producer) → reviewer.
- `<prefix>` (Workflow v1) — Start (setup) → ReviewLoop pointing at `<prefix>-inner` → on Approved exits cleanly; on Exhausted routes to the HITL escalation.

Use when the loop body needs to read parsed/seeded workflow vars that don't exist on the raw input artifact.

### ReviewLoop pair (`review-loop-pair`)

The canonical "draft, critique, finalize" pattern. Materializes:
- `<prefix>-trigger` (Agent v1) — minimal kickoff that forwards the workflow input verbatim.
- `<prefix>-producer` (Agent v1) — pins `@codeflow/producer-base`. Has a TODO slot for "what you're producing".
- `<prefix>-reviewer` (Agent v1) — pins `@codeflow/reviewer-base`. Declares `Approved` + `Rejected` outputs. Has a TODO slot for acceptance criteria.
- `<prefix>-inner` (Workflow v1) — Start (producer) → reviewer with ports `Approved` / `Rejected` / `Failed`.
- `<prefix>` (Workflow v1) — Start (trigger) → ReviewLoop pointing at `<prefix>-inner` with `loopDecision="Rejected"`, `reviewMaxRounds=5`, `rejectionHistory.enabled=true`.

Run the outer workflow with sample input and the loop iterates against the producer/reviewer pair without further wiring. Both agent prompts have explicit `TODO` slots the author fills in for their domain.

## API surface

- `GET /api/workflow-templates` — list templates with `id`, `name`, `description`, `category`.
- `GET /api/workflow-templates/{id}` — single template summary.
- `POST /api/workflow-templates/{id}/materialize` — body: `{ "namePrefix": "my-feature" }`. Returns `201 Created` with the entry workflow's `Location` header and a payload listing every entity created.

Authorization: `WorkflowsRead` for list/get, `WorkflowsWrite` for materialize.

## Prefix validation

Prefixes are trimmed and restricted to `[A-Za-z0-9_-]` so they can't collide with reserved separators in workflow / agent keys (`:`, `/`, `.`). Whitespace-only prefixes are rejected with 400.

## Collision handling

Before materializing, the framework queries the DB for any planned key (`<prefix>`, `<prefix>-producer`, etc.) and refuses with `409 Conflict` if any collide. The response carries a `conflicts: [{ kind, key }]` array so the editor can render which keys collided and prompt the operator for a different prefix.

This pre-flight check guarantees materialization is atomic in spirit: a partial set of orphan agents never lingers from a failed run.

## Adding new templates

Templates live in `CodeFlow.Api/WorkflowTemplates/`. To add one:

1. Create `<NewTemplate>.cs` next to `EmptyWorkflowTemplate.cs` and `ReviewLoopPairTemplate.cs`.
2. Implement a `Build()` static returning a `WorkflowTemplate` record:
   - Stable `Id`, human-readable `Name` + `Description`, a `Category` enum.
   - `Materialize` async delegate that creates the entities via the supplied repositories.
   - `PlanKeys` synchronous delegate listing the entity keys the template intends to create — the materializer pre-flight uses this for collision detection.
3. Register in `WorkflowTemplateRegistry`'s default constructor (`new WorkflowTemplateRegistry(EmptyWorkflowTemplate.Build(), ReviewLoopPairTemplate.Build(), YourTemplate.Build())`).

Templates currently only describe new versions at v1. Bumping a template doesn't bump existing materialized workflows — those are operator-owned.

## Status: complete

All Phase 4 templates (S3 framework + S4 ReviewLoop pair + S5 HITL approval gate + S6 Setup → loop → finalize + S7 Lifecycle wrapper) are shipped. The remaining S2 (HITL form presets in the editor's New-Form picker) is a frontend card and out of the templates framework's scope.
