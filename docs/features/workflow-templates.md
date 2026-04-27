# Workflow templates (S3 framework + S4-S7 templates)

The "New from Template" picker on the Workflows page collapses 30 minutes of wiring into 30 seconds. Templates ship with the platform; authors pick one, give it a name prefix, and get a fully-wired workflow + agents materialized at v1.

## Available templates

### Empty workflow (`empty-workflow`)

A single Start node + placeholder agent. Materializes:
- `<prefix>-start` (Agent v1) — placeholder system prompt.
- `<prefix>` (Workflow v1) — Start node wired to the agent.

Use when you want full structural control without scaffolding decisions.

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

## Status: S5/S6/S7

S5 (HITL approval gate), S6 (Setup → loop → finalize), and S7 (Lifecycle wrapper) are designed but not yet shipped — they depend on S2 (HITL form presets, frontend) for the passthrough preset. The framework supports them once the prerequisites land.
