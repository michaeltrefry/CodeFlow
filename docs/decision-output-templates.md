# Decision-based Output Templates

Decision-based output templates rewrite an agent's artifact *after* a decision is submitted, per decision value. Different decision → different template. Useful when the same agent needs to emit Markdown for approvals, a JIRA comment for rejections, or a structured JSON packet for a custom port.

> Companion references: [prompt-templates.md](prompt-templates.md) documents the Scriban engine and sandbox — decision templates use the exact same engine, sandbox, and syntax. [workflows.md](workflows.md) explains where `context.*` and `workflow.*` come from.

Decision-output templates are **distinct** from:
- **Prompt templates** — the system/user prompt fed *into* an LLM agent (see [prompt-templates.md](prompt-templates.md)).
- **HITL form templates** (the legacy `outputTemplate` field on a HITL agent) — defines the reviewer's *form schema*, i.e. which fields the UI renders and what's submitted. Decision-output templates format the *output* written after submission.

## 1. Declaring templates

Templates live on the agent configuration under `decisionOutputTemplates`, keyed by output port name (case-insensitive). `*` is the wildcard fallback.

```json
{
  "provider": "openai",
  "model": "gpt-5",
  "decisionOutputTemplates": {
    "Approved": "[APPROVED] {{ output.headline }}\n\n{{ output.summary }}",
    "Rejected": "Rejected: {{ output.headline }}\nReason: {{ output.reason }}",
    "*": "{{ decision }}: {{ output }}"
  }
}
```

Applies to every agent kind that produces a decision: LLM, HITL, Start, and ReviewLoop children. Template keys are port names; the set of valid port names is whatever the agent declares in its `outputs` (see [port-model.md](port-model.md)).

**Limits** (enforced on save): at most 32 entries, each template ≤ 16 KiB, keys matching `[A-Za-z0-9_-]{1,64}` or the literal `*`.

## 2. Resolution order

For a decision on port `P`:

1. If a routing script called `setOutput(…)` → script override wins (unchanged).
2. Else if `decisionOutputTemplates[P]` exists → render it.
3. Else if `decisionOutputTemplates["*"]` exists → render it.
4. Else → no rewrite; downstream receives the original agent submission.

The original submission is always preserved in the artifact store. Only `DecisionRecord.OutputRef` and the downstream `InputRef` are swapped to point at the rendered artifact.

## 3. Render context

The Scriban scope exposed to every decision template:

| Name | Type | Notes |
|---|---|---|
| `decision` | string | The submitted decision name (the agent-declared port name the LLM/HITL picked at submit time). |
| `outputPortName` | string | The effective port name the saga is routing on (may differ from `decision` for custom HITL options). |
| `context.<path>` | nested object | Workflow-local inputs (saga's local `context` bag). |
| `workflow.<path>` | nested object | Workflow-global inputs (propagated across parent/subflow boundaries). |

LLM-only:

| Name | Type | Notes |
|---|---|---|
| `output` | string *or* object/array | The raw agent submission. Parsed to a nested object/array when the submission is a JSON object or array; otherwise a plain string. |
| `input.<path>` | nested object | The upstream artifact content that this agent consumed as input. |

HITL-only:

| Name | Type | Notes |
|---|---|---|
| `input.<field>` | any | The reviewer's form submission — each form field keyed by its placeholder name. |
| `reason` | string | Free-text reason from the submission. |
| `reasons` | array of strings | Multi-select reason tags. |
| `actions` | array of strings | Multi-select action tags. |

Syntax is full Scriban — conditionals, loops, filters, whitespace trimming — see [prompt-templates.md §3](prompt-templates.md).

## 4. Sandbox

Identical to prompt templates:

- 50 ms render timeout.
- `LoopLimit` 1000, `RecursiveLimit` 64.
- 1 MiB output size cap (`LimitToString = 1,000,000`).
- No `TemplateLoader` — `include` / `import` directives fail.
- Relaxed member access — unresolved variables render as empty.

## 5. Failure semantics

Any `PromptTemplateException` during render:

- **Saga path (LLM, Start, ReviewLoop child):** the saga appends the decision record with the original output ref, then transitions to `Failed` with `FailureReason = "Decision output template failed: {detail}"`. Downstream dispatch is skipped.
- **HITL submit endpoint:** returns `422 UnprocessableEntity { error: "Decision output template failed: …" }`. The pending task stays `Pending` so the reviewer can correct and resubmit.
- **Preview endpoint (`POST /api/agents/templates/render-preview`):** returns `422 { error: "…" }`.

The original agent submission is preserved in the artifact store either way — no data loss on render failure.

## 6. Worked examples

### 6.1 LLM agent with Approved/Rejected pair

Agent returns JSON like `{"headline":"Ship it","summary":"…","reason":"…"}`:

```json
{
  "decisionOutputTemplates": {
    "Approved": "## {{ output.headline }}\n\n{{ output.summary }}\n\n_Approved by {{ context.reviewer }}._",
    "Rejected": "❌ Rejected\n\n**{{ output.headline }}**\n\n{{ output.reason }}"
  }
}
```

### 6.2 HITL agent with a custom decision option

The HITL form template declares `{{decision:Approved|NeedsChanges}}`. On the `NeedsChanges` port, send the reviewer's notes downstream as a changelist:

```json
{
  "decisionOutputTemplates": {
    "NeedsChanges": "### Changes requested\n\n{{ input.notes }}\n\n{{ if actions.size > 0 }}Action items:\n{{ for a in actions }}- {{ a }}\n{{ end }}{{ end }}"
  }
}
```

### 6.3 Wildcard fallback

When every decision ends with a uniform wrapper:

```json
{
  "decisionOutputTemplates": {
    "*": "[{{ decision }}] {{ output }}"
  }
}
```

## 7. Preview endpoint

For live authoring:

```
POST /api/agents/templates/render-preview
{
  "template": "[{{ decision }}] {{ input.feedback }}",
  "mode": "hitl",
  "decision": "Approved",
  "outputPortName": "Approved",
  "fieldValues": { "feedback": "looks good" },
  "context": { "headline": "Ship" }
}
```

Returns `{ "rendered": "…" }` on success or `422 { "error": "…" }` on render failure. The endpoint shares the exact sandbox and context-builder the saga and HITL submit use in production, so previews match what authors will see at runtime.

## 8. Precedence summary

```
setOutput()  >  decisionOutputTemplates[port]  >  decisionOutputTemplates["*"]  >  no rewrite
```
