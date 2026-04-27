# Rejection-history accumulation (P3)

ReviewLoop nodes can opt in to a framework-managed accumulator that captures the loop-decision artifact each rejection round. Replaces the hand-rolled "Pattern 2" accumulator scripts that authors wrote in every reviewer pre-P3.

## Configuration

On the **ReviewLoop node** (not the inner agent):

```json
"rejectionHistory": {
  "enabled": true,
  "maxBytes": 32768,
  "format": "Markdown"
}
```

Fields:
- `enabled` — required. `true` opts in. Without this entire object, the loop behaves as it did before P3.
- `maxBytes` — optional, defaults to `32768`. When the accumulator exceeds the budget, the framework drops oldest rounds first (FIFO). UTF-8 boundaries are preserved.
- `format` — optional, defaults to `Markdown`. `Json` is the alternative — produces a `[{round, body}, ...]` JSON array.

## Runtime behavior

1. Every time the inner workflow exits on the loop's `loopDecision` port (typically `Rejected`) and the loop has rounds left, the framework reads the loop-decision artifact and appends it to the reserved workflow variable `__loop.rejectionHistory`.
2. The accumulator is idempotent: re-delivering the same `(round, body)` overwrites the round entry instead of stacking duplicates.
3. The next iteration's child agents see the value as `{{ rejectionHistory }}` (un-prefixed Scriban alias) AND `{{ workflow.__loop.rejectionHistory }}` (the underlying bag value). Use the alias.

## Markdown format example

```markdown
## Round 1
The plan is missing acceptance criteria for phase 2.

## Round 2
Phase 3 has tasks that depend on uncreated artifacts.
```

## JSON format example

```json
[
  { "round": 1, "body": "The plan is missing acceptance criteria for phase 2." },
  { "round": 2, "body": "Phase 3 has tasks that depend on uncreated artifacts." }
]
```

## Reserved namespace

`__loop.rejectionHistory` is a reserved framework key. The runtime rejects any `setWorkflow('__loop.…', …)` call from agent or script (`ProtectedVariables.IsReserved`). The save-time validator (`protected-variable-target`) catches static violations on mirror / port-replacement targets too.

## Author migration

Pre-P3 reviewer prompts often included an output script like:

```javascript
if (output.decision === 'Rejected') {
  var prior = workflow.rejectionHistory || '';
  var entry = '## Round ' + round + '\n' + output.text;
  setWorkflow('rejectionHistory', prior ? prior + '\n\n' + entry : entry);
}
```

Replace with `rejectionHistory.enabled: true` on the ReviewLoop node and reference `{{ rejectionHistory }}` in the in-loop agent prompts. See `workflows/impl-plan-v4-package.json` for the canonical migration.

## Telemetry

When the feature fires (every accumulation), an activity tag `codeflow.feature.last_round_reminder.auto_injected` is recorded; the planned `IAuthoringTelemetry.FeatureUsed("rejection-history")` event is reserved (the constant `BuiltInFeatureIds.RejectionHistory` is exported but the call is deferred until the orchestrator can reach the API-side telemetry sink).
