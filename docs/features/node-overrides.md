# Node-level agent property overrides

Tuning a workflow often means one node needs a slightly different agent than the
shared agent definition provides — one extra tool, a bigger token budget, a
faster model. Node overrides let a workflow author set a small set of
invocation-time properties **on the node**, without editing the agent and
without forking it.

## Mental model

An override is a pure **invocation-time overlay**:

- It applies **only to that node, in that workflow**. The same agent used by
  another node — or another workflow — is unaffected.
- It does **not** create a new agent version and does **not** fork the agent.
  The node stays linked to its source agent and keeps inheriting future updates.
- Every field is **inherit-by-default**. An unset override field means "use the
  agent's value". Clearing a field reverts that property to the agent's own
  setting.

It only applies to the agent-bearing single-agent node kinds: **Agent, Hitl,
Start, and Goal**. (Swarm contributor/coordinator/synthesizer slots and ForEach
child dispatch are out of scope for v1.)

## What's overridable

| Property | Overlays |
|---|---|
| **Model provider + model** | `AgentInvocationConfiguration.Provider` / `Model` — a both-or-neither pair |
| **Max output tokens** | `AgentInvocationConfiguration.MaxTokens` |
| **Max tool calls** | `InvocationLoopBudget.MaxToolCalls` |
| **Max wall-clock duration** | `InvocationLoopBudget.MaxLoopDuration` (set in seconds) |
| **Max consecutive non-mutating tool calls** | `InvocationLoopBudget.MaxConsecutiveNonMutatingCalls` |
| **Additional tools** | extra host / MCP tools, **additive only** — see below |

Provider and model are coupled: set both or neither. The save-time validator
rejects a half-set pair.

## Additive tools

The tools override is **additive only**. It never replaces or removes the
agent's role-derived tool set — it grants *extra* tools on top. The persisted
override list (`AgentInvocationOverrides.AdditionalToolIdentifiers`) is the
additive delta only: host tool names and/or `mcp:<server>:<tool>` identifiers.

In the editor's tools picker, the tools the agent already resolves through its
role render **checked and disabled** ("inherited") — you can't uncheck them.
Only the tools you check on top are written into the override.

At runtime the additive tools are unioned into the agent's resolved tool set and
flow through the authority envelope's Role tier, so a deliberately-narrowing
tenant / workflow / context tier can still restrict them — node overrides extend
the agent's effective tool set, they do not bypass a restricting envelope.

## How it differs from in-place agent edit

These are complementary tools for two different jobs.

| | Node overrides (this feature) | [In-place agent edit](../agent-in-place-edit.md) |
|---|---|---|
| Scope | One node, one workflow | A forked agent, reusable |
| Creates a version? | No | Yes — a workflow-scoped fork |
| Surface | Node inspector → **Overrides** tab | Right-click node → full agent modal |
| What can change | 6 invocation properties | The entire agent config |
| Stays linked to source agent | Yes | No — fork lineage |

Overrides are the lightweight "nudge". Fork is the heavyweight "this agent is
now materially different". If you find yourself overriding the system prompt or
reshaping outputs, you want a fork, not an override.

## Where to set it

In the workflow editor, select an Agent / Hitl / Start / Goal node and open the
**Overrides** tab in the node inspector. Each scalar field shows the agent's own
value as an `Inherited: …` placeholder; leave a field blank to inherit. The
tools picker shows every available tool with the agent's role tools locked.

## Contract

`AgentInvocationOverrides` (in `CodeFlow.Contracts`) is the overlay record — all
fields nullable, null = inherit. It lives on `WorkflowNode.AgentOverrides`,
persisted as the `AgentOverridesJson` column, threaded onto the
`AgentInvokeRequested` message, and merged in `AgentInvocationConsumer` (and the
in-process `GoalNodeDispatcher`) on top of the agent's stored config before the
invocation runs. Workflow-package export / import carries `AgentOverrides`, so a
node's overrides survive an export → import round-trip.

## See also

- [In-place agent editing](../agent-in-place-edit.md) — the heavyweight
  fork-based counterpart.
- `workflows/node-overrides-demo-v1-package.json` — a reference workflow whose
  Agent node carries an `agentOverrides` overlay (different model, larger
  budgets, two extra host tools).
