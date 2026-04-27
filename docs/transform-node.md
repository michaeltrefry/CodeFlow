# Transform Node

A `Transform` node renders a Scriban template against workflow + context variables and the structured upstream input, producing a transformed artifact. It eliminates agent and Logic-script overhead for deterministic data shaping — when the only thing a node needs to do is reshape data, a Transform is cheaper, faster, and easier to read than either an LLM call or a routing script.

> Companion references: [prompt-templates.md](prompt-templates.md) documents the Scriban engine, sandbox, and syntax — Transform nodes use the exact same engine. [port-model.md](port-model.md) explains the implicit `Failed` port. [workflows.md](workflows.md) explains where `context.*` and `workflow.*` come from.

Transform nodes are **distinct** from:
- **Prompt templates** — the system/user prompt fed into an LLM agent.
- **Decision-output templates** — agent-side rewrites of an artifact *after* a decision is submitted (see [decision-output-templates.md](decision-output-templates.md)).
- **Logic nodes** — script-driven routing/transformation when the work cannot be expressed declaratively.

## 1. Node shape

| Field | Type | Required | Notes |
|---|---|---|---|
| `kind` | `Transform` | yes | New `WorkflowNodeKind` value. |
| `template` | string | yes | Scriban body. Must parse on save. |
| `outputType` | `"string"` \| `"json"` | no | Default `"string"`. When `"json"`, the rendered text is parsed as JSON before becoming the output payload. |
| `inputScript` | string \| null | no | Same `setInput` slot as Agent/HITL/Subflow/ReviewLoop. Optional — most Transform nodes won't need it. |
| `outputScript` | string \| null | no | Same `setOutput` slot as Agent/HITL/Subflow/ReviewLoop. Optional. |

Ports:
- One `in` port (standard).
- One declared output port: `Out`.
- Implicit `Failed` (universal, never declared in `outputPorts`).

Agent-related fields (`agentKey`, `agentVersion`), subflow fields (`subflowKey`, `subflowVersion`), and review-loop fields (`reviewMaxRounds`, `loopDecision`) are **not** populated on Transform nodes.

## 2. Execution semantics

When the saga reaches a Transform node:

1. If `inputScript` is set, run it under existing `setInput` semantics to shape the structured input the template will see.
2. Build the Scriban scope from workflow variables, context variables, and the structured input. Same sandbox, timeout, and resource caps as prompt-template rendering.
3. Render `template`. Render error → `Failed`.
4. If `outputType === "json"`, parse the rendered string. Parse error → `Failed`.
5. The rendered (and optionally parsed) value becomes the payload on `Out`.
6. If `outputScript` is set, run it under existing `setOutput` semantics before the payload lands on `Out`.

All errors route through the implicit `Failed` port. There are no other failure modes the author needs to declare.

## 3. Render context

| Name | Type | Notes |
|---|---|---|
| `input.<path>` | nested object | The structured upstream artifact this node consumed (same shape an agent would see). |
| `context.<path>` | nested object | Workflow-local inputs (saga's local `context` bag). |
| `workflow.<path>` | nested object | Workflow-global inputs (propagated across parent/subflow boundaries). |

The Scriban sandbox is identical to prompt templates — no filesystem, no network, no reflection. See [prompt-templates.md](prompt-templates.md) for the full list of allowed built-ins.

## 4. Authoring

The node config panel reuses:
- Monaco editor with Scriban autocomplete (E3).
- Live preview pane (VZ3) — accepts a sample structured input fixture and shows rendered output. When `outputType === "json"`, the pane also surfaces parse errors.
- An `outputType` radio toggle (String / JSON).

Validation on save:
- `template` is required and must Scriban-parse.
- If `outputType === "json"`, the live preview's sample render must JSON-parse — surfaced as an authoring warning, not a save block (the live sample may legitimately differ from runtime data).

## 5. When to use Transform vs. Logic vs. Agent

| Need | Use |
|---|---|
| Reshape structured data deterministically (rename fields, project subsets, format Markdown/JSON) | **Transform** |
| Conditional branching across multiple ports based on input shape | **Logic** |
| Anything requiring reasoning, summarization, or external knowledge | **Agent** |

A Transform node with `outputType: "json"` is the canonical replacement for a Logic node whose only purpose was `setOutput(JSON.parse(renderedTemplate))`.

## 6. Out of scope (v1)

- Multiple output ports / template-driven port selection.
- Async or streaming rendering.
- Custom Scriban functions beyond what prompt templates already expose.
- File or network I/O from inside templates.
