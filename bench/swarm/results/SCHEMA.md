# Results CSV schema

One row per trace. The operator (or a future `tools/swarm-bench/` script) populates this from the `/api/traces/{id}/token-usage` endpoint and the trace's saga state.

File location: `bench/swarm/results/<run-date>.csv`. One CSV per bench run; the run date is the day the traces were executed (UTC).

## Columns

| Column | Source | Type | Notes |
|---|---|---|---|
| `trace_id` | trace UUID | guid | Stable. The join key against the scoring CSV. |
| `variant` | operator | `V1` \| `V2` \| `V3` | V1 = single-agent baseline; V2 = hand-authored Sequential subflow; V3 = Swarm node (P2 only). |
| `request` | operator | `A` \| `B` | A = PRD synthesis (Request A); B = architecture tradeoff (Request B). |
| `run_index` | operator | int (1..N) | Run number within the (variant × request) cell. N=3 for v1. |
| `model` | agent config | string | Pinned model for the run, e.g. `gpt-5.4` or `claude-sonnet-4-6`. Should match across all variants for one bench. |
| `workflow_id` | trace detail | string | Workflow key. |
| `workflow_version` | trace detail | int | Pinned version on the trace. |
| `started_at_utc` | saga state | ISO 8601 | `WorkflowSagaStateEntity.CreatedAtUtc`. |
| `completed_at_utc` | saga state | ISO 8601 | `WorkflowSagaStateEntity.UpdatedAtUtc` for terminal states. |
| `wall_clock_ms` | computed | int | `(completed_at_utc - started_at_utc).TotalMilliseconds`. |
| `call_count` | token-usage API | int | `Total.CallCount` — number of LLM round-trips on the trace. |
| `total_input_tokens` | token-usage API | int | `Total.Totals["input_tokens"]`. |
| `cached_input_tokens` | token-usage API | int | `Total.Totals["input_tokens_details.cached_tokens"]` (0 if absent). |
| `total_output_tokens` | token-usage API | int | `Total.Totals["output_tokens"]`. |
| `total_reasoning_tokens` | token-usage API | int | `Total.Totals["output_tokens_details.reasoning_tokens"]` (0 if absent). |
| `final_state` | saga state | `Done` \| `Failed` \| `Aborted` | `WorkflowSagaStateEntity.CurrentState` mapped to the terminal label. |
| `failure_reason` | saga state | string \| empty | `WorkflowSagaStateEntity.FailureReason` if non-null. |
| `score_coverage` | scoring CSV (after reconciliation) | int 1–5 | Final reconciled score. |
| `score_coherence` | scoring CSV | int 1–5 | Final reconciled score. |
| `score_specificity` | scoring CSV | int 1–5 | Final reconciled score. |
| `score_actionability` | scoring CSV | int 1–5 | Final reconciled score. |
| `score_perspective` | scoring CSV | int 1–5 | Final reconciled score. |
| `score_total` | computed | int 5–25 | Sum of the five dimensions. |
| `scorer_human` | scoring CSV | int 5–25 | Human's pre-reconciliation total. |
| `scorer_llm` | scoring CSV | int 5–25 | LLM judge's pre-reconciliation total. |
| `per_node_json` | token-usage API | json | `ByNode` rollup verbatim. Keep in a JSON column so future fields don't require schema migration. |
| `per_scope_json` | token-usage API | json | `ByScope` rollup verbatim. |

## Header row

The first line of every results CSV is exactly the contents of [`header.csv`](header.csv) so producers and consumers don't drift.

## Conventions

- Timestamps are ISO 8601 with explicit `Z` suffix (UTC). The token-usage endpoint already emits with `DateTimeKind.Utc` set; honour that.
- Empty-but-required cells: leave the cell empty (no `null`, no `0`). The schema reader should treat empty cells as missing.
- JSON columns: serialise to a compact JSON string (no embedded newlines), single-line. CSV-escape per RFC 4180 (double-quote the cell, double up internal quotes).
- The CSV is the durable record. The harness summary (`<run-date>-summary.md`) is derived from it; the CSV wins on conflict.

## Aggregates

Computed from the CSV, written to `bench/swarm/results/<run-date>-summary.md`:

- Per (variant × request): mean and stdev of `score_total`, mean per dimension, mean `wall_clock_ms`, mean `total_input_tokens + total_output_tokens`.
- Per variant (averaged across requests): same.
- Variance flag: if stdev of `score_total` within a cell is ≥ 4 points, flag the cell as "high variance — interpret with care."
- Decision-gate evaluation per `docs/swarm-bench-harness.md` §"Decision gates."
