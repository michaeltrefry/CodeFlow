# CodeFlow Kickoff Implementation Plan

This document is the in-repo reference for the initial CodeFlow buildout.

The Kanban `CodeFlow` project and `Kickoff` epic remain the operational source of truth for execution status and slicing. This file exists to make the plan easy to review inside the repository while the codebase is still being created.

This reference should be read alongside the epic documents `High-Level Plan` and `Implementation Plan`, which capture the more detailed locked-in design choices behind the kickoff backlog.

## Project Summary

CodeFlow is an event-driven multi-agent workflow system built around:

- agent runtime orchestration
- provider-agnostic model invocation
- workflow routing based on structured agent decisions
- asynchronous execution over RabbitMQ
- persistent configuration and trace state in MariaDB
- an API and UI for authoring agents, workflows, and runs

## Initial Product Goals

The kickoff backlog is aimed at delivering these baseline capabilities:

1. Run an agent with a provider-neutral runtime abstraction.
2. Support tool-calling, including host tools, MCP-backed tools, and sub-agent fan-out.
3. Persist versioned agent and workflow configuration.
4. Execute workflow handoffs asynchronously through a bus-backed orchestration layer.
5. Track workflow progress through a saga with bounded review loops and escalation.
6. Expose authoring and trace-monitoring flows through an authenticated API and UI.

## Architectural Direction

### Runtime

The runtime is centered around a provider-neutral `Agent.Invoke(config, input)` entry point. The main internal building blocks are:

- `IModelClient` for provider adapters
- a unified chat/tool-call message model
- `ContextAssembler` for prompt assembly
- `ToolRegistry` plus multiple tool providers
- `InvocationLoop` for model and tool-call turn-taking

The runtime returns a typed `AgentDecision`, and that decision contract is a load-bearing boundary for the rest of the system.

### Persistence

MariaDB is the system database. EF Core is used for:

- versioned agent configuration storage
- workflow and edge persistence
- saga state persistence
- outbox support for reliable event publication

Artifacts are stored outside the database through an `IArtifactStore`, with a filesystem implementation planned first.

### Orchestration

MassTransit plus RabbitMQ provides asynchronous execution. The initial orchestration shape is:

1. An `AgentInvokeRequested` event is published.
2. A consumer resolves config and input, runs the agent, stores output, and publishes `AgentInvocationCompleted`.
3. A workflow saga receives completion events and routes to the next agent or terminal state.

### Local Integration Environment

The first non-trivial integration environment should be Docker-based so local validation matches the expected runtime shape as early as possible.

That environment should provide:

- MariaDB for EF Core persistence and outbox validation
- RabbitMQ with the management UI enabled for bus and saga testing
- any broker features the current phase actually depends on

For the current kickoff plan, saga execution itself does not require a special RabbitMQ plugin. If we later adopt RabbitMQ transport-level message scheduling or delayed delivery, we should enable the delayed-exchange plugin explicitly at that point.

### API and UI

The API provides authenticated CRUD-style management for agents, workflows, and traces. The UI provides:

- agent authoring
- workflow graph editing
- run submission
- live trace monitoring
- later, human-in-the-loop intervention and ops tooling

## Delivery Phases

### Phase 1: Runtime foundation

Scope:

- solution skeleton and tests
- unified message model
- OpenAI adapter
- Anthropic adapter
- context assembly
- tool registry
- invocation loop
- agent facade with sub-agent and MCP tool providers

Primary outcome:

Create a local, testable runtime that can execute a configured agent invocation and produce a structured decision.

Cards:

- `[1.1]` Solution skeleton + Runtime/Tests projects
- `[1.2]` `IModelClient` + unified message model
- `[1.3]` OpenAI provider adapter
- `[1.4]` Anthropic provider adapter
- `[1.5]` `ContextAssembler`
- `[1.6]` `IToolProvider` registry + host tool provider
- `[1.7]` `InvocationLoop`
- `[1.8]` Agent facade + sub-agent + MCP providers

### Phase 2: Persistence foundation

Scope:

- Docker-based local integration environment
- EF Core setup for MariaDB
- versioned agent repository
- workflow and edge repository
- artifact store
- initial saga schema

Primary outcome:

Provide durable storage for config, artifacts, and the state required by later orchestration work.

Cards:

- `[2.0]` Docker dev/test stack
- `[2.1]` Persistence project + EF Core + MariaDB
- `[2.2]` Agent config schema + versioned repo
- `[2.3]` Workflow + edge schema + repo
- `[2.4]` `IArtifactStore` + filesystem implementation
- `[2.5]` Saga state schema stub

### Phase 3: Bus-backed execution

Scope:

- shared event contracts
- MassTransit and RabbitMQ wiring
- invocation consumer
- integration tests across bus, db, and artifact storage

Primary outcome:

Move agent execution out of direct in-process orchestration into reliable message-driven processing.

Cards:

- `[3.1]` Contracts project + event contracts
- `[3.2]` MassTransit DI + RabbitMQ + EF outbox
- `[3.3]` `AgentInvocationConsumer`
- `[3.4]` Bus integration test

### Phase 4: Workflow saga and routing

Scope:

- saga definition
- decision-based handoff routing
- round-count bounding and escalation
- end-to-end saga integration tests

Primary outcome:

Support multi-agent workflows with deterministic routing, pinned versions, and bounded review loops.

Cards:

- `[4.1]` WorkflowSaga definition
- `[4.2]` Decision to handoff routing
- `[4.3]` Round-count bounding + escalation
- `[4.4]` End-to-end saga integration test

### Phase 5: Product surface

Scope:

- authenticated API
- agent, workflow, and trace endpoints
- live trace streaming
- Angular UI scaffold
- agent authoring UI
- workflow graph editor
- trace monitor
- HITL review support

Primary outcome:

Make CodeFlow usable as a product, not just a backend runtime and orchestration engine.

Cards:

- `[5.1]` API skeleton + generic OAuth/OIDC
- `[5.2]` Agents REST
- `[5.3]` Workflows REST
- `[5.4]` Traces REST
- `[5.5]` Live trace SSE endpoint
- `[5.6]` Angular SPA scaffold
- `[5.7]` Agent config authoring UI
- `[5.8]` Workflow graph editor UI
- `[5.9]` Run submission + live monitor
- `[5.10]` HITL review UI

### Phase 6: Operations and hardening

Scope:

- OpenTelemetry correlation
- DLQ inspection and retry
- retry-with-context semantics
- company-specific auth swap-in

Primary outcome:

Improve observability, reliability, and integration readiness for real operational usage.

Cards:

- `[6.1]` OpenTelemetry + trace ID correlation
- `[6.2]` DLQ surface + retry UX
- `[6.3]` Retry-with-context semantics
- `[6.4]` Company auth swap-in

## Key Design Constraints

The kickoff epic implies several important constraints that should be treated as intentional unless explicitly revised:

- `AgentDecision` is a stable boundary contract across runtime, contracts, saga routing, and UI.
- Agent and workflow configs are versioned append-only records, not mutable in place.
- Tool access is policy-controlled through allowlists and category ceilings.
- Reliable execution depends on an outbox-backed messaging pattern.
- Workflow routing is driven by decision outputs rather than hard-coded orchestration branches.

## Carry-Over Constraints

The existing epic planning documents already resolved several design questions. Those resolutions should carry forward unless explicitly changed:

- Sub-agent spawning is in-process fan-out inside the parent invocation via `await Task.WhenAll(...)`, not a nested workflow trace and not a bus-level orchestration concern.
- The saga pins `agentKey -> version` at first reference so in-flight runs are not reshaped by later config edits.
- Decisions and handoff are separate concerns: agents emit `AgentDecision`, while workflow edges decide who runs next.
- Events carry identifiers and artifact references, not embedded agent config or large payloads.
- Routing remains pattern-matched on structured decisions and optional discriminators; there is no separate routing expression language in the kickoff design.

## Known Risks And Validation Points

These are the highest-leverage early validation points in the current plan:

- Stand up the Docker dev/test stack before Phase 2 and 3 integration-style validation so MariaDB and RabbitMQ assumptions are exercised in the same environment the team will use day to day.
- Build the OpenAI adapter against the Responses API rather than Chat Completions.
- Validate MassTransit EF outbox behavior early with Pomelo MariaDB.
- Prototype the SSE plus bus event streaming shape before heavy UI investment.
- Keep `AgentDecision` narrow and durable to avoid cascading refactors.

## Immediate Recommended Execution Order

The next slice to start with is:

1. `[1.1]` Create the .NET solution skeleton.
2. `[1.2]` Lock in the shared runtime message model.
3. Implement the supporting runtime pieces around that model, especially the provider adapters and tool-provider abstractions in `[1.3]`, `[1.4]`, and `[1.6]`.
4. Treat `[1.7]` as the shape-locking moment for `AgentDecision` and the invocation loop once those surrounding pieces are in place.
5. Complete the facade and provider composition in `[1.8]`.

This order reduces rework because provider adapters, contracts, and orchestration all depend on the runtime model being stable, while still acknowledging that `[1.7]` depends on the surrounding runtime pieces being real enough to exercise.

Before Phase 2 integration work begins, complete `[2.0]` so the Docker-backed MariaDB and RabbitMQ environment is available for the persistence, bus, and saga slices that follow.

## Resolved Planning Answers

The latest kickoff review clarified these decisions and assumptions:

- The initial scaffold should standardize on .NET 10 and the modern `CodeFlow.slnx` solution format rather than .NET 9 and legacy `.sln`.
- The first OpenAI integration should target the Responses API.
- Tool parameter schema enforcement for the first runtime pass should stay minimal: schemas and arguments need to be valid and well-formed, without introducing heavy semantic validation beyond what is required for safe serialization and provider/tool interoperability.
- MCP functionality can start as the thinnest useful MVP path. The earliest valuable milestone is publishing a message and having agents process it end to end; richer MCP breadth can follow after runtime, orchestration, and UI-based configuration/dispatch are working.
- UI work should prioritize operational clarity first, while still building toward full configuration and dispatch capability. There is no current instruction to narrow delivery scope for cost reasons.
- The shared local integration environment should be Docker-based. It needs MariaDB and RabbitMQ up front so persistence, outbox, consumer, and saga work can be validated in a realistic shape.
- RabbitMQ does not need a special plugin for the basic MassTransit saga path in this kickoff plan. Only transport-level scheduling or delayed delivery would require the delayed-exchange plugin, so that should stay conditional rather than assumed.

## Usage

Use this document as the repo-local narrative reference.

Use Kanban cards for:

- execution order
- current status
- validation notes
- scope adjustments
- newly discovered work
