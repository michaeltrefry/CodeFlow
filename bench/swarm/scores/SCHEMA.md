# Scoring CSV schema

One CSV per scorer per run. The operator hands each scorer (human + LLM judge) a CSV containing one row per output, with the variant labels stripped — only a **blinded label** identifies each row. The scorer fills in the dimension scores and a one-paragraph rationale, and returns the file.

## File layout

- `bench/swarm/scores/<run-date>-human.csv` — human scorer's worksheet.
- `bench/swarm/scores/<run-date>-llm.csv` — LLM judge's worksheet (one row per LLM-judge invocation).
- `bench/swarm/scores/<run-date>-blinding.csv` — operator-only file mapping `blinded_label` → `trace_id` + variant. **Do not share with scorers until reconciliation.**

## Columns (worksheet handed to scorer)

| Column | Filled by | Notes |
|---|---|---|
| `blinded_label` | operator | Random letter (A, B, C, ...) assigned per output. Stable for one run; different per run. |
| `request` | operator | `A` or `B`. Scorers DO see the request — they need it to apply the rubric. |
| `output` | operator | The variant's terminal artifact, verbatim. Embedded multi-line content; CSV-escape per RFC 4180. |
| `score_coverage` | scorer | 1–5 per `bench/swarm/scoring/rubric.md`. |
| `score_coherence` | scorer | 1–5. |
| `score_specificity` | scorer | 1–5. |
| `score_actionability` | scorer | 1–5. |
| `score_perspective` | scorer | 1–5. |
| `rationale` | scorer | One paragraph (80–200 words). For the LLM judge this is the JSON `rationale` field; for the human it's free text. |

## Columns (blinding file — operator only)

| Column | Notes |
|---|---|
| `blinded_label` | The letter the scorer saw. |
| `trace_id` | Real trace UUID. |
| `variant` | `V1` \| `V2` \| `V3`. |
| `request` | `A` \| `B`. |
| `run_index` | Run number within the (variant × request) cell. |

## Headers

The first lines of the worksheet and blinding files are exactly the contents of [`worksheet-header.csv`](worksheet-header.csv) and [`blinding-header.csv`](blinding-header.csv) respectively.

## Conventions

- **One blinded letter per output, NOT per variant.** With 12 outputs (V1+V2 × 2 requests × N=3) the labels are A through L. With 18 (V1+V2+V3 × 2 × 3) they are A through R. Don't reuse the same letter across two outputs from the same variant.
- **Randomise the order** before handing the CSV to a scorer. The default sort by `blinded_label` is fine if the labels themselves were assigned at random (don't assign A to all V1s).
- **Do not include** `trace_id`, `variant`, `run_index`, or any token/latency/timing column on the scorer's worksheet. Quality is scored independently of cost.
- **`output` is the entire artifact**, with formatting preserved. For the V2 / V3 swarm variants this is the synthesizer's final output, not the chain of contributions.

## Reconciliation

After both scorers return their CSVs:

1. Operator merges `human` + `llm` worksheets via `blinded_label`.
2. For each `blinded_label`, compute per-dimension `|human - llm|`. If any dimension is ≥2 apart, mark the row for re-review and re-score that dimension on both sides.
3. Apply the `blinding.csv` to map back to `trace_id` + `variant`.
4. Write the final per-output dimension scores to `bench/swarm/results/<run-date>.csv`. The reconciliation file itself is kept under `bench/swarm/scores/` for audit but does not feed the aggregate.
