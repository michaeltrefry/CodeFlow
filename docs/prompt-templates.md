# Prompt Templates

Prompt templates shape what an agent actually sees. The runtime compiles each template through the [Scriban](https://github.com/scriban/scriban) engine — a lightweight, sandboxed text templating language whose `{{ name }}` substitution is a strict superset of the original CodeFlow placeholder syntax. Existing prompts that only use `{{ name }}` continue to render exactly as before; authors who need conditionals, loops, or filters now have them available.

> Companion references: [workflows.md](workflows.md) describes how template variables get populated (workflow inputs, `global`, per-round review counters); [review-loop.md](review-loop.md) documents the `round`/`maxRounds`/`isLastRound` bindings.

## 1. The basics

Most templates only need plain substitution.

```
Review {{artifact}} for the {{audience}} team.

Focus on:
- correctness
- clarity
```

Whitespace around the identifier is optional — `{{artifact}}` and `{{ artifact }}` are equivalent. Variables resolve case-insensitively against the template variables the runtime assembles (see below).

A placeholder that cannot be resolved is emitted **as a literal** — the token `{{ foo }}` stays in the output untouched. This is deliberate: authors sometimes seed templates with placeholders they fill in later. Scriban expressions (conditionals, loops, filters) do not get this treatment — a genuinely malformed template produces a `PromptTemplateException` with a readable error message.

## 2. Variable namespaces

Every render exposes these namespaces:

| Namespace | Source | Example |
|---|---|---|
| `input` | The raw user/upstream payload for this agent invocation. | `{{ input }}` |
| `input.<path>` | Flattened members of the input, if it parses as JSON. | `{{ input.summary }}` |
| `context.<path>` | Flattened workflow-local inputs (the saga's local `context` bag). | `{{ context.gitRepo }}` |
| `global.<path>` | Flattened shared context propagated across parent/subflow boundaries. | `{{ global.resolvedSpec.engine }}` |
| `round`, `maxRounds`, `isLastRound` | Populated only when the agent runs inside a [`ReviewLoop`](review-loop.md). `isLastRound` is a boolean; `round` and `maxRounds` are integers. | `{{ if isLastRound }}Ship it.{{ end }}` |
| Configured variables | Values declared on the agent itself (`AgentConfiguration.Variables`). | `{{ reviewerTone }}` |

JSON objects and arrays arriving through `context.*`, `global.*`, or `input.*` are reshaped into nested structures at render time — so `{{ context.target.repo }}` and `{{ for item in context.items }}...{{ end }}` both work when the source data is nested.

## 3. Syntax at a glance

| Construct | Scriban syntax |
|---|---|
| Variable | `{{ name }}` |
| Dotted path | `{{ context.target.repo }}` |
| Conditional | `{{ if isLastRound }}...{{ else }}...{{ end }}` |
| Negation | `{{ if !isLastRound }}...{{ end }}` |
| Comparison | `{{ if round >= maxRounds }}...{{ end }}` |
| Loop | `{{ for item in context.items }}- {{ item }}{{ end }}` |
| Loop index | `{{ for item in context.items }}{{ for.index }}: {{ item }}{{ end }}` |
| Capture into a variable | `{{ capture summary }}...{{ end }}` |
| Filter / pipe | `{{ context.title | string.upcase }}` |
| String literal | `{{ "literal text" }}` |
| Whitespace trimming | `{{- name -}}` (strips surrounding whitespace) |

See the [Scriban language reference](https://github.com/scriban/scriban/blob/master/doc/language.md) and [built-in filter list](https://github.com/scriban/scriban/blob/master/doc/builtins.md) for the full grammar. The runtime does **not** register custom filters beyond what Scriban ships with.

## 4. Worked examples

### 4.1 Simple substitution with a fallback placeholder

```
Summarize the following artifact for the {{ audience }} team.

Draft:
{{ input }}

Additional notes: {{ notes }}
```

If `notes` isn't provided by the agent configuration or workflow context, the literal string `{{ notes }}` is emitted — the human reviewer or a later agent fills it in. Useful for "scratch-pad" sections that are optional.

### 4.2 Branching on a ReviewLoop round

```
Review the draft below. Return `Approved` if ready to ship, or `Rejected` with actionable
feedback.

{{ if isLastRound }}
This is round {{ round }} of {{ maxRounds }} — the final round. If the draft is not ready,
return `Failed` with a concise reason. Do not return `Rejected`; there is no next round.
{{ else }}
Round {{ round }} of {{ maxRounds }}. Favor specific, actionable critiques over broad
rewrites — there will be another revision pass.
{{ end }}

Draft:
{{ input }}
```

### 4.3 Iterating over a JSON array from `context.*`

Assuming the workflow launches with an input of `{ "items": ["alpha", "bravo", "charlie"] }`:

```
Review the following checklist items and flag anything missing:

{{ for item in context.items }}
- {{ item }}
{{ end }}
```

Renders to:

```
Review the following checklist items and flag anything missing:

- alpha
- bravo
- charlie
```

For arrays of objects, members are addressable directly: `{{ for entry in context.entries }}- {{ entry.title }} ({{ entry.owner }}){{ end }}`.

### 4.4 Conditional guidance driven by input shape

```
{{ if input.priority == "high" }}
URGENT: {{ input.title }}
Drop all other work and address this before anything else in the backlog.
{{ else }}
{{ input.title }}
{{ end }}
```

## 5. Sandbox limits

Templates run inside a Scriban sandbox with the following limits enforced per render:

- **Wall-clock budget**: 50 ms. Templates that exceed this budget abort and surface a `PromptTemplateException`.
- **Loop iterations**: capped at 1,000. A runaway `{{ for i in 1..9999999 }}` fails fast.
- **Recursion depth**: capped at 64.
- **Output size**: capped at ~1 MB of concatenated text.
- **No filesystem access**: `{{ include }}` and `{{ import }}` fail. Template composition happens through skills and the agent/workflow graph, not through the template engine.
- **No runtime eval**: there is no `eval` equivalent. All templates are static data parsed at render time.

These mirror the guardrails used by the Logic-node JavaScript sandbox and keep a misbehaving template from hanging an agent invocation.

## 6. Escaping literal delimiters

To emit literal `{{` or `{%` characters without triggering the engine, wrap them in a string literal:

```
Authors reference variables with {{ "{{" }} name {{ "}}" }}.
```

Renders to:

```
Authors reference variables with {{ name }}.
```

For a short block of literal text containing many delimiters, a single string literal is usually easier than repeated escapes:

```
{{ "{{#if isLastRound}} ship {{/if}}" }}
```

Unresolved legacy `{{ name }}` placeholders do **not** need escaping — they pass through to the output as literals automatically. Escaping only matters when a literal `{{` should survive alongside other templating syntax.
