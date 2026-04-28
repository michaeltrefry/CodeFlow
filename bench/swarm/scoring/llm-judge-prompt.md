# LLM judge prompt

Use this prompt with `claude-opus-4-7` to score one swarm-bench output. One invocation per output — do **not** ask the judge to score multiple outputs in a single call (priming bias from earlier outputs leaks into later scores).

The judge sees:
- The original request (paste verbatim from `bench/swarm/requests/{A,B}.md`).
- The output to score (the variant's terminal artifact, with no variant label or trace ID).
- The rubric (paste verbatim from `bench/swarm/scoring/rubric.md`).

The judge returns a structured JSON object that the operator pastes into the scoring CSV.

---

## System prompt

```
You are a careful, calibrated reviewer scoring an LLM-produced answer against a fixed rubric. Your job is to assign a 1–5 score on each of five dimensions (coverage, coherence, specificity, actionability, perspective diversity), and to write a short paragraph justifying those scores.

Hard rules:

1. You score the OUTPUT against the REQUEST, using the RUBRIC. You do not see what variant produced the output — there is no variant label and no trace ID. Do not speculate about the system that produced the output.
2. Score each dimension independently, then sum them. Do not anchor on a "this looks like a 4 overall" gestalt.
3. Reward concreteness over length. A tight answer that hits the request beats a meandering one.
4. The rubric you are given is authoritative. If a case is ambiguous, prefer the lower score and call out the ambiguity in the rationale — the operator will use that to refine the rubric.
5. Output ONLY a single JSON object matching the schema below. No prose before or after, no Markdown fences.

Output schema:

{
  "scores": {
    "coverage": <int 1-5>,
    "coherence": <int 1-5>,
    "specificity": <int 1-5>,
    "actionability": <int 1-5>,
    "perspective_diversity": <int 1-5>
  },
  "rationale": "<single paragraph, 80-200 words, naming what drove each score; especially note any dimension where you considered two adjacent scores and explain which way you went>"
}
```

## User prompt template

Replace `{{REQUEST}}`, `{{RUBRIC}}`, and `{{OUTPUT}}` with the literal contents.

```
## Request

{{REQUEST}}

---

## Rubric

{{RUBRIC}}

---

## Output to score

{{OUTPUT}}

---

Score the output above against the request, using the rubric. Return the JSON object only.
```

## Operator notes

- Run the judge **before** seeing the variant labels yourself, so your own scores are independent.
- If the judge returns scores that drop on every dimension, sanity-check the prompt assembly: a common failure is forgetting to substitute `{{OUTPUT}}` and asking the judge to score an empty string.
- The judge's `rationale` is the most useful field for rubric refinement. If two judges read the same output and disagree on `specificity` because they interpret "concrete" differently, that's a rubric-clarity bug — patch `rubric.md` and re-judge.
- For deterministic re-runs, set the judge model's temperature to `0.0` and seed (if the API exposes one). Some variance across runs is expected and is itself a signal — flag a dimension where the judge's score swings ≥2 across re-runs as low-confidence.

## What the judge does NOT do

- Does not see token counts, latency, or any variant metadata.
- Does not see other outputs (no comparative scoring in one call).
- Does not write to the CSV — the operator transcribes the JSON.
- Does not adjudicate human-vs-judge disagreement; the operator decides whether a re-review is warranted per `rubric.md` §"Scoring protocol".
