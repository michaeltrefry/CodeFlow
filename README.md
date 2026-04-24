# CodeFlow

CodeFlow is a multi-agent workflow platform for authoring, running, and reviewing agent-driven work. It combines a .NET API and worker, an event-driven orchestration layer, persistence for versioned configuration and trace history, and an Angular UI for agents, workflows, traces, HITL review, and system settings.

This repository is no longer just scaffolded. The current tree includes:

- agent configuration and test surfaces
- a visual workflow editor with agent, logic, HITL, subflow, and review-loop nodes
- trace submission, streaming trace detail, and HITL queue screens
- settings for MCP servers, skills, agent roles, git hosts, and LLM providers
- a Docker-based local stack for the API, worker, UI, MariaDB, RabbitMQ, and Aspire dashboard

## Solution layout

- `CodeFlow.Api` - ASP.NET Core API and HTTP endpoints
- `CodeFlow.Worker` - background worker for orchestration and long-running processing
- `CodeFlow.Host` - shared hosting, transport, observability, workspace, and dead-letter wiring
- `CodeFlow.Orchestration` - workflow saga/state-machine behavior
- `CodeFlow.Runtime` - agent runtime, model clients, MCP integration, and workspace tooling
- `CodeFlow.Persistence` - EF Core data model, repositories, migrations, and artifact storage
- `CodeFlow.Contracts` - shared contracts and message types
- `codeflow-ui` - Angular 20 frontend
- `docs` - feature and design references
- `starter_workflows` - packaged starter workflow definitions

## Prerequisites

- .NET 10 SDK
- Node.js with npm
- Docker Desktop or compatible Docker runtime

## Quick start with Docker

From the repo root:

```bash
docker network create mcp-shared
docker compose up --build
```

Main local endpoints:

- UI: [http://localhost:4200](http://localhost:4200)
- API: [http://localhost:5080](http://localhost:5080)
- RabbitMQ management: [http://localhost:15673](http://localhost:15673)
- Aspire dashboard: [http://localhost:18888](http://localhost:18888)

Default local infrastructure credentials:

- MariaDB: `codeflow` / `codeflow_dev` on `127.0.0.1:3306`
- RabbitMQ: `codeflow` / `codeflow_dev` on `127.0.0.1:5673`, vhost `codeflow`

Optional model/secrets configuration can be provided through environment variables. See [dot_env_sample.txt](/D:/repos/michael/CodeFlow/dot_env_sample.txt).

To stop the stack:

```bash
docker compose down
```

To stop it and remove Docker-managed volumes:

```bash
docker compose down -v
```

## Local development

### Backend

The API applies database migrations on startup and, by default, listens on `http://localhost:5080`.

```bash
dotnet restore CodeFlow.slnx
dotnet run --project CodeFlow.Api
dotnet run --project CodeFlow.Worker
```

### Frontend

The Angular app proxies `/api` to `http://localhost:5080`.

```bash
cd codeflow-ui
npm install
npm start
```

## Testing

Run the .NET test projects:

```bash
dotnet test CodeFlow.slnx
```

Run frontend checks from `codeflow-ui`:

```bash
npm run typecheck
npm run build
```

## Core concepts

- **Agents** are versioned configurations for model/provider, prompts, tools, skills, and decision behavior.
- **Workflows** are versioned directed graphs that route work between agents and logic nodes.
- **Traces** capture workflow execution, artifacts, decisions, evaluations, and HITL pauses.
- **Subflows** let one workflow call another as a reusable unit.
- **Review loops** support bounded draft-review-revise iterations with round-aware context.
- **MCP + host tooling** let agents call external tools and local workspace capabilities under policy.

## Docs worth reading

- [Workflow model and editor behavior](./docs/workflows.md)
- [Subflows and workflow composition](./docs/subflows.md)
- [Review loop design](./docs/review-loop.md)
- [Prompt templates](./docs/prompt-templates.md)
- [Agent in-place editing](./docs/agent-in-place-edit.md)
- [Local integration stack notes](./docs/local-integration-stack.md)
- [Kickoff implementation plan](./docs/kickoff-implementation-plan.md)

## Notes

- The root `docker-compose.yml` is the most accurate source for local container topology and ports.
- The UI has its own focused notes in [codeflow-ui/README.md](/D:/repos/michael/CodeFlow/codeflow-ui/README.md).
