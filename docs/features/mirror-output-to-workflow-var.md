# Mirror output to workflow variable (P4)

Replaces the "Pattern 1" output script that pre-P4 authors wrote on every producer agent — `setWorkflow('currentPlan', output.text)` — with a one-checkbox declarative config on the agent node.

## Configuration

On the **Agent / Hitl / Start node** (not the agent definition):

```json
"mirrorOutputToWorkflowVar": "currentPlan"
```

When the field is set and non-empty, the runtime:

1. Reads the agent's output text once after `submit` returns.
2. Writes that text into the named workflow variable BEFORE the node's output script runs (so output scripts can read `workflow.currentPlan`).
3. Continues with the normal output-script and routing path.

## Why "before output script"?

This was [Open Question 2 in the requirements doc](../authoring-dx/) — and the answer is "before". A producer that mirrors AND has an output script (e.g., to compute a derived value from the just-mirrored plan) needs the mirror to land before the script so the script can read it. Authors who only wanted the mirror don't notice the timing; authors who needed both ordering get the right order automatically.

## Reserved namespace

Mirror targets that resolve to a reserved namespace (`__loop.*`, `workDir`, `traceId`) are silently dropped at runtime. The save-time `protected-variable-target` validator surfaces the misconfiguration as an Error before save.

## Author migration

Pre-P4:

```json
"outputScript": "setWorkflow('currentPlan', output.text);"
```

After:

```json
"mirrorOutputToWorkflowVar": "currentPlan"
// outputScript field removed
```

Behaviorally equivalent. Saves a script you never have to read again.

## See also

- [P5 replace-artifact-from-workflow-var](replace-artifact-from-workflow-var.md) for the symmetric "consume from a workflow var on a specific port".
- `workflows/impl-plan-v4-package.json` for a full migration example.
