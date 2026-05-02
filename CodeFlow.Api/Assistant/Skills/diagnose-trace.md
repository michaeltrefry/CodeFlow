---
key: diagnose-trace
name: Diagnose trace
description: Procedure for invoking diagnose_trace and formatting its verdict.
trigger: user asks "why did this fail" / "what went wrong" / "explain this trace" or any open-ended diagnostic question about a specific trace.
---

# Diagnose trace

Load this skill when the user wants you to explain what happened in a
trace — failures, anomalies, "why is this slow", "what did node X
emit". The diagnosis goes through one tool: `diagnose_trace`.

## When to invoke

When the user asks "why did this fail?" / "what went wrong?" / "explain
this trace" / any open-ended diagnostic question about a specific trace,
invoke `diagnose_trace` with the trace id.

On a trace page, the `<current-page-context>` block already carries the
trace id — pass it directly without asking the user. If the user pasted
a trace URL (`/traces/{guid}`) or a bare trace id, use that.

The tool composes the saga header, decision timeline, logic evaluations,
and token usage into a structured verdict with anomaly heuristics already
applied server-side (`long_duration`, `token_spike`, `logic_failure`).
It works on completed traces too — it returns an empty `failingNodes`
array but may still flag anomalies worth reviewing.

Read-only; no chip — `diagnose_trace` just reads.

## Format the diagnosis as

1. **One-sentence lead** drawn from the verdict's `summary` field. State
   the headline finding without preamble.
2. **Failing node + cause.** Name the node id and agent (if any), state
   the failure reason, link to the trace inspector via the `deepLink`
   field and to the agent editor via `agentDeepLink`.
3. **Evidence.** Cite the relevant anomalies (token spike, long duration,
   logic failure) by their `evidence` numbers from the verdict payload.
4. **Recommended next action.** Surface the `suggestions[]` items as
   concrete links the user can click — replay-with-edit (`/traces/{id}`),
   agent review (`/agents/{key}`), inspect node I/O via `get_node_io`.

Keep the response tight. Four short sections, no padding. The user's
diagnostic question is usually targeted; spend tokens on evidence, not
restating context they already have.

## Follow-up questions

For follow-up "show me node X's actual output" / "what was the input to
node Y" questions, chain `get_node_io` with the node id from the verdict.
That tool returns the recorded artifact body for the requested node.

**Do NOT re-invoke `diagnose_trace` for the same trace within a turn —
its result is stable.** If the user asks a follow-up that needs different
data (e.g., "show me the timeline"), reach for `get_trace_timeline` or
`get_node_io`, not another diagnose call.

## When the verdict says nothing failed

If `failingNodes` is empty and no anomalies surface but the user is still
asking "why did X happen", the trace is healthy from the runtime's
perspective. Either:
- The user's framing was wrong (the trace they pointed at isn't the one
  they meant) — ask which trace they intend.
- The behavior they're surprised by is by design (e.g., a ReviewLoop
  reaching `Exhausted` is the loop's documented behavior). Explain the
  shape; offer Replay-with-Edit if a substitution would change the
  outcome.
