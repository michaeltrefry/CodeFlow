# Swarm bench harness — operator guide

This directory holds the artifacts for running the swarm-bench head-to-head described in [`docs/swarm-bench-harness.md`](../../docs/swarm-bench-harness.md). The harness doc is the design; the files here are the **operator's runbook**.

## What's here

```
bench/swarm/
├── README.md                              ← this file
├── requests/
│   ├── A.md                               ← Request A: PRD from sparse brief
│   └── B.md                               ← Request B: architecture tradeoff
├── scoring/
│   ├── rubric.md                          ← canonical 5-dimension Likert rubric
│   └── llm-judge-prompt.md                ← system + user prompt for the LLM judge (claude-opus-4-7)
├── results/
│   ├── SCHEMA.md                          ← results CSV schema
│   └── header.csv                         ← canonical results CSV header row
└── scores/
    ├── SCHEMA.md                          ← scoring + blinding CSV schemas
    ├── worksheet-header.csv               ← scorer worksheet header row
    └── blinding-header.csv                ← operator-only blinding-map header row
```

## Run protocol

This is the harness doc's §"Run protocol" with concrete file paths. Step 0 (sc-80 prereq) is shipped.

### 1. Confirm variant workflows are imported

```
GET /api/workflows/swarm-bench-baseline/1            # V1
GET /api/workflows/swarm-bench-sequential/1          # V2
# V3 (Swarm node) is gated on sc-43 and not part of P1.
```

If either returns 404, import the package via `POST /api/workflows/package/apply` from `workflows/swarm-bench-baseline-v1-package.json` / `workflows/swarm-bench-sequential-v1-package.json`.

### 2. Pick the model (and pin all variants to it)

Per `docs/swarm-bench-harness.md`, all variants in a single bench run use the **same model**. The library defaults to `openai/gpt-5.4`. Edit each agent and bump versions if you want a different model — V1 has 2 agents, V2 has 3 agents. **Don't mix models within a variant.**

Record the chosen model in the results CSV's `model` column for every row.

### 3. Run the traces

For each `(variant, request)` cell, kick off **N=3** traces via the `/traces/submit` UI, pasting the request markdown into the input field:

| Variant | Workflow | Request A trace IDs | Request B trace IDs |
|---|---|---|---|
| V1 | `swarm-bench-baseline` | × 3 | × 3 |
| V2 | `swarm-bench-sequential` | × 3 | × 3 |

= **12 traces total** for the P1 bench (V1 + V2). Capture each trace ID into a scratch file as you go — the order matters less than the completeness.

V3 will add another 6 traces (3 per request) for P2; that's sc-44, not sc-41.

### 4. Pull token-usage and saga timestamps; populate `results/<run-date>.csv`

For each trace ID:

```
GET /api/traces/{id}              → started_at_utc, completed_at_utc, final_state, failure_reason, model, workflow_id, workflow_version
GET /api/traces/{id}/token-usage  → call_count, total_input_tokens, cached_input_tokens, total_output_tokens, total_reasoning_tokens, per_node_json, per_scope_json
```

Compute `wall_clock_ms` = `(completed_at_utc - started_at_utc).TotalMilliseconds`. Leave the `score_*` and `scorer_*` columns empty for now — they get filled during reconciliation.

### 5. Strip variant labels and randomise; build scorer worksheets

For each output:
1. Read the agent's terminal artifact body (V1: the answering agent's last message; V2: the synthesizer's last message).
2. Assign a random blinded letter (A through L for the P1 12-output run).
3. Fill `bench/swarm/scores/<run-date>-blinding.csv` with `(blinded_label, trace_id, variant, request, run_index)` — **do not share this file** with scorers until step 7.
4. Fill `bench/swarm/scores/<run-date>-human.csv` and `<run-date>-llm.csv` with the worksheet columns: `blinded_label, request, output`. Leave the `score_*` columns empty.

Randomise the order of rows before handing each worksheet to its scorer.

### 6. Score

- Hand `<run-date>-human.csv` to yourself (Michael) with `bench/swarm/scoring/rubric.md`.
- For each row in `<run-date>-llm.csv`, run the judge once per row with the prompt in `bench/swarm/scoring/llm-judge-prompt.md`. Paste each row's `output` into the `{{OUTPUT}}` slot, the matching request into `{{REQUEST}}`, and the rubric into `{{RUBRIC}}`. Transcribe the JSON return values into the row's `score_*` columns.

### 7. Reconcile

1. Merge the two scorer CSVs by `blinded_label`. For each row + dimension, compute `|human - llm|`. Any dimension ≥2 apart triggers a re-review on both sides.
2. After all re-reviews settle, apply `<run-date>-blinding.csv` to map blinded letters back to trace IDs.
3. Write the reconciled per-dimension scores into `results/<run-date>.csv`'s score columns. Compute `score_total`.

### 8. Write the summary

`bench/swarm/results/<run-date>-summary.md` — per the harness doc §"Aggregates", with the table and a one-paragraph interpretation. Attach to [sc-41](https://app.shortcut.com/trefry/story/41) (P1) or sc-44 (P2).

## Decision gates

Per `docs/swarm-bench-harness.md` §"Decision gates":

- **After sc-41**: if V2 doesn't show ≥10% mean-score lift over V1 *or* burns >3× the tokens without compensating quality → **stop before Phase 2**.
- **After sc-44**: if V3 isn't materially better than V2 → flag for design review before pursuing Phase 3 (Coordinator).

## What this directory does NOT contain

- A runner CLI. The harness doc explicitly defers `tools/swarm-bench/` until after we've run the bench enough times to justify it. For v1, the protocol above is "scripts you run by hand."
- Live trace data. Run-specific CSVs land here as you produce them; nothing is checked in until a real run is summarised.
- USD cost. Tokens only — see the harness doc.
