# Replace artifact from workflow variable on port (P5)

Replaces the "Pattern 2" output script — `if (output.decision === 'Approved') { setOutput(workflow.currentPlan); }` — with per-port declarative config.

## Configuration

On the **Agent / Hitl / Start node**:

```json
"outputPortReplacements": {
  "Approved": "currentPlan"
}
```

When the agent submits on a port that's a key in this map, the runtime:

1. Lets the output script (if any) run first — so the script can mutate the workflow var.
2. Reads the named workflow variable.
3. Writes a fresh artifact with that content.
4. Returns it as the override output ref. Per-port replacement takes precedence over a `setOutput()` call from the output script.
5. Ports without a binding flow the agent's verbatim submission unchanged.

## Use case

Reviewer agents typically submit a brief approval rationale ("Looks good — clear acceptance criteria") that's useful for the trace history but shouldn't flow downstream as the artifact. Pre-P5, authors wrote:

```javascript
// Output script on the reviewer node
if (output.decision === 'Approved') {
  setOutput(workflow.currentPlan);
}
```

After:

```json
"outputPortReplacements": {
  "Approved": "currentPlan"
}
```

The reviewer's rationale is logged in the trace's decision history; the downstream node receives `workflow.currentPlan` (typically populated by an upstream P4 mirror).

## Reserved namespace

Same as P4: targets in `__loop.*` / `workDir` / `traceId` are caught by `protected-variable-target` at save.

## Behavioral note

If the workflow variable named in the binding doesn't exist when the port fires, the runtime falls back to the agent's verbatim artifact rather than substituting an empty string. The save-time `WorkflowVarDeclarationRule` (VZ2) flags missing references when you opt in to `workflowVarsReads/Writes` declarations.

## See also

- [P4 mirror-output-to-workflow-var](mirror-output-to-workflow-var.md) — typically paired with P5: producer mirrors → reviewer replaces from the same key.
- `workflows/impl-plan-v4-package.json` for a full migration example.
