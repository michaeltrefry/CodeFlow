# Swarm-bench scoring rubric

Canonical rubric used by both the human scorer and the LLM judge. Single source of truth — the harness doc (`docs/swarm-bench-harness.md`) and the LLM judge prompt (`bench/swarm/scoring/llm-judge-prompt.md`) reference this file rather than duplicating it.

## Dimensions

Five dimensions, scored independently, 5-point Likert (1–5). **Total** = sum (5–25), reported per-output and averaged across the runs of a (variant × request) cell.

### 1. Coverage

How fully did the output address what the request asked for?

| Score | Means |
|--:|---|
| 5 | All aspects addressed; no obvious omissions. Every section the request named is present and substantive. |
| 4 | Almost all aspects addressed. One minor omission (a sub-bullet, a non-critical section) but no major gap. |
| 3 | Most aspects addressed. One named section missing or treated as a single sentence. |
| 2 | Material omissions. A request section is missing entirely or treated as a placeholder ("TBD"). |
| 1 | Major sections of the request unaddressed; the output reads as if the model only saw part of the prompt. |

### 2. Coherence

Internal consistency of the output. No contradictions, no fragmentary sections.

| Score | Means |
|--:|---|
| 5 | Tight. Sections reinforce each other; tradeoffs declared in one place are honoured throughout; no internal contradiction. |
| 4 | Mostly coherent. One mild tension (e.g., a non-goal that conflicts subtly with an acceptance criterion) but the overall recommendation holds. |
| 3 | Some friction. A tradeoff resolved one way in one section is questioned elsewhere; the reader has to reconcile. |
| 2 | Notably contradictory. Two sections argue opposite directions, or a recommendation reverses without acknowledging the reversal. |
| 1 | Self-contradictory or fragmentary. Sections don't connect; the output reads as if assembled from incompatible drafts. |

### 3. Specificity

Concrete vs. generic. Real names, numbers, examples, file paths — vs. platitudes that could apply to any project.

| Score | Means |
|--:|---|
| 5 | Concrete throughout. Names existing CodeFlow surfaces (entities, endpoints, file paths, port names); cites real numbers (token thresholds, byte limits); offers concrete examples. |
| 4 | Mostly concrete. One or two stretches that lean on generic-engineering language but the rest grounds itself. |
| 3 | Mixed. The output cites concrete things in some places and falls back on platitudes ("we should be careful about edge cases") in others. |
| 2 | Mostly generic. Reads like advice that could apply to any product; few specific names, numbers, or examples. |
| 1 | Generic platitudes only. No concrete commitments. The output could be cut and pasted into any project. |

### 4. Actionability

Could a reasonable engineer ship from this output? Or does the reader still have to do the work?

| Score | Means |
|--:|---|
| 5 | Ready to execute or refine. A reader can identify the next concrete step (which file to open, which entity to add, which decision to bring to a meeting) without re-reading. |
| 4 | Mostly actionable. The next step is clear for the main thread but one sub-area (e.g., an open question) is left vague. |
| 3 | Partially actionable. The output frames the problem well but stops short of committing to a path; the reader has to make the hard call themselves. |
| 2 | Restatement-heavy. The output mostly summarises the question and gestures at a path; the reader is doing most of the work. |
| 1 | Restating the problem; no path forward. |

### 5. Perspective diversity *(the swarm thesis)*

Does the output reflect multiple framings — multiple roles, multiple stakeholder concerns, multiple tradeoff axes? This is the dimension the swarm protocol is supposed to lift over a single-agent baseline.

| Score | Means |
|--:|---|
| 5 | Clearly distinct perspectives surface. PM/eng/QA/ops concerns each show up; tradeoff axes name competing values (speed vs. safety, ergonomics vs. correctness); the output reads like several skilled minds informed it. |
| 4 | Multiple perspectives present, but one or two dominate. A reader gets the recommended path AND at least one credible counter-framing. |
| 3 | Two distinct framings noted, but they don't engage each other; the output picks one and gestures at the other. |
| 2 | One framing only, with brief acknowledgement of "other concerns" without naming them. |
| 1 | Single voice / single framing. No alternative considered. |

## Total

`total = coverage + coherence + specificity + actionability + perspective_diversity` — range 5–25.

Per-cell aggregate (over N=3 runs): mean and stdev of `total`, plus mean of each dimension.

## Scoring protocol

1. **Variant labels are blinded.** Scorers (human and LLM) see only a per-output blinded letter (A, B, C, ...) — never `V1`, `V2`, `V3`, or workflow keys. The label-to-variant map is held aside until reconciliation.
2. **Score per dimension first, then sum.** Don't anchor on a "this looks like a 4 overall" gestalt; assign each dimension before computing the total.
3. **Disagreement gate**: any dimension where the human and LLM differ by **≥2** is re-reviewed. Either the human or the LLM might be miscalibrated; the re-review either flips one of them or surfaces a real ambiguity in the rubric (which we then patch).
4. **Don't score against the rubric in two passes.** One pass per output. Re-reading the same output multiple times leaks gestalt back into the per-dimension scores.

## Anti-patterns

- **Don't reward word count.** A tight 600-word PRD that hits all seven sections beats a meandering 1500-word one. Length is not a dimension.
- **Don't penalise abstention** in the swarm output. If a contributor in the V2/V3 chain abstained (`ROLE: abstain`), the synthesizer should ignore it — the final answer is what we score, not the chain. (Per-agent abstention behaviour goes in a separate analysis if at all.)
- **Don't compare V1 vs V2 outputs side-by-side while scoring.** Score independently. Comparison happens at aggregate time.
