# Request B — Architecture tradeoff analysis

> Used by the swarm-bench harness ([sc-41](https://app.shortcut.com/trefry/story/41) / `docs/swarm-bench-harness.md`). One of two synthesis-shaped requests. Paste the **mission** below into the variant workflow's input field; the variant's answer is the recommendation.

## Mission

We want **workflow-level token budgets** in CodeFlow — a way for the workflow author to put a ceiling on how many tokens a single trace of a workflow may spend, so a runaway loop or oversized prompt doesn't burn an open-ended bill before someone notices. Three options are on the table; recommend one.

### Options

**(a) Hard saga ceiling.**
Single token budget configured on the workflow root. The saga tracks running totals as `TokenUsageRecord` rows arrive. When the budget is exceeded, the saga immediately transitions to `Failed` on the implicit Failed port. No per-node visibility; one loud failure.

**(b) Soft per-node warnings.**
Each node reports its tokens at completion (already captured via `TokenUsageRecord`). The runtime emits a structured warning event (and surfaces a chip in the trace inspector) when a node's per-node budget is exceeded, but the saga does not abort. The workflow author can wire a Logic node downstream of the budget event to gate, route, or terminate explicitly. Per-node budgets are configured on each node; an unconfigured node has no warning threshold.

**(c) Both** — soft per-node warnings PLUS a saga-level hard ceiling. The warnings give visibility into where tokens are being spent; the hard ceiling stops a runaway loop. Both are configured: workflow-level ceiling at the root, per-node thresholds on individual nodes.

### Constraints

- Budgets are CodeFlow configuration. No external policy engine.
- Tokens — not USD — are the unit (CodeFlow does not compute cost).
- The token-counting plumbing already exists (`TokenUsageRecord`, the trace-token-usage endpoint, `TokenUsageRecorded` SSE events).
- Subflow / ReviewLoop iterations roll up into the parent saga's running total — a child saga does not have its own ceiling.
- This is a v1; do not design a hierarchical budget tree (per-team, per-org).

## Task for the agent (or panel)

Recommend **(a)**, **(b)**, or **(c)**, with an explicit tradeoff matrix across at least these axes:

- **Enforcement strictness** — does it actually stop the spend?
- **Author ergonomics** — how much config does the author have to think about?
- **Failure-mode predictability** — when the budget is hit, is the resulting trace state easy to reason about?
- **Observability impact** — does the operator know *where* the tokens went, or just that the trace failed?
- **Implementation complexity** — how much new runtime code; how much migration risk.

Aim for ~400–900 words. The matrix can be a Markdown table or prose; pick whichever serves the recommendation. Justify the recommendation in 1–2 paragraphs after the matrix.
