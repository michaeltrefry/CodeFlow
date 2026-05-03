# Sub-agents — Design

**Story:** [Sub-agent fan-out: editor surface + role/auth model + UX](https://app.shortcut.com/trefry/story/571) (sc-571)
**Status:** v1 shipped — anonymous workers parameterised at spawn time

This doc is the contract for the `spawn_subagent` runtime primitive. Sub-agents are not pre-configured slots: the parent agent supplies a per-call system prompt and input for each spawned worker, and the spec on the parent agent controls provider/model/concurrency.

## When to use what

| Primitive | Use when |
| --- | --- |
| **Sub-agents** | One model needs to delegate sub-tasks to cheaper or more focused workers it parameterises at runtime. Parent reasons; workers are anonymous helpers exposed as a tool. |
| **Swarm node** | Multiple peers run an explicit protocol (Sequential, Coordinator) authored at workflow-design time. See [`swarm-node.md`](./swarm-node.md). |
| **Subflow** | Compose another workflow inline with its own ports, nodes, and decision ledger. See [`subflows.md`](./subflows.md). |

## Parent-agent config

`AgentInvocationConfiguration.SubAgents: SubAgentConfig?` enables the runtime to surface the `spawn_subagent` tool. All fields are optional — leave any null to inherit the parent's value.

```jsonc
{
  "subAgents": {
    "provider": "anthropic",         // null/omit → inherit parent's provider
    "model": "claude-haiku-4-5",     // null/omit → inherit
    "maxConcurrent": 4,              // 1..32, default 4
    "maxTokens": 8000,               // null/omit → inherit
    "temperature": 0.2               // 0..2, null/omit → inherit
  }
}
```

Validator bounds: `maxConcurrent ∈ [1, 32]`, `maxTokens ∈ [1, 200_000]`, `temperature ∈ [0, 2]`.

## `spawn_subagent` tool

The runtime exposes this tool when `SubAgents` is set on the parent. Tool category: `SubAgent`. Implicit allow on `MergeToolAccessPolicy` — callers never grant it through a role/envelope.

```jsonc
{
  "invocations": [
    {
      "systemPrompt": "You are a focused worker. Reply as JSON {findings: string[]}.",
      "input": "Read the README and list anything that contradicts the docs."
    },
    {
      "systemPrompt": "You are a critic. Score 1–5 and explain.",
      "input": "Score the proposed approach in <input/>"
    }
  ]
}
```

Each invocation builds an ad-hoc child `AgentInvocationConfiguration`:

| Child field | Source |
| --- | --- |
| `Provider` | `spec.Provider ?? parent.Provider` |
| `Model` | `spec.Model ?? parent.Model` |
| `SystemPrompt` | `invocation.systemPrompt` (LLM-authored at spawn time) |
| `MaxTokens` | `spec.MaxTokens ?? parent.MaxTokens` |
| `Temperature` | `spec.Temperature ?? parent.Temperature` |
| `SubAgents` | `null` (children cannot recursively spawn) |

The tool returns one JSON object per invocation, in request order:

```jsonc
[
  { "input": "...", "output": "...", "decision": { "kind": "Completed", "...": "..." } },
  ...
]
```

## Concurrency

Calls within a single `spawn_subagent` tool call are throttled to `spec.MaxConcurrent` via a `SemaphoreSlim`. The cap is per tool call, not per parent invocation — successive `spawn_subagent` calls each get a fresh budget.

## Tool inheritance

Sub-agents inherit the parent's resolved tool set (`ResolvedAgentTools`) — host tools, MCP tools, and the role-derived allow-list flow through unchanged. **There is no per-spawn role assignment in v1.** If a worker needs different tools than the parent, that's a v2 conversation.

## Non-goals (v1)

- Per-spawn role/tool grants. Workers inherit; tighter scoping comes later.
- Structured output-format enforcement. Parent describes the response shape inside `systemPrompt`; the runtime does not parse against a JSON Schema. ([Q2 option (b)](https://app.shortcut.com/trefry/story/571).)
- Recursive spawning. Children get `SubAgents: null` so they cannot spawn workers themselves; depth is therefore capped at 1 by construction.
- Cycles. No slot keys → no graph → no cycle detection needed.
- Cost ceilings in USD. Tokens only.

## Authoring surface

The agent editor (Prompt & output tab, LLM agents only) exposes the spec via:

- "Enable sub-agents" checkbox
- Provider override (blank inherits)
- Model override (blank inherits)
- Max concurrent (1..32, default 4)
- Max tokens / temperature overrides (blank inherits)
