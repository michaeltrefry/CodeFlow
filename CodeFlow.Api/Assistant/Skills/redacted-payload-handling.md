---
key: redacted-payload-handling
name: Redacted-payload handling
description: Recognize and act on `_redacted: true` placeholders in your transcript history.
trigger: you see a `tool_use` Input or `tool_result` body with `_redacted: true` in your conversation history.
---

# Redacted-payload handling

Workflow packages are 50–200 KB of JSON. If every prior turn's
`set_workflow_package_draft` / `save_workflow_package` Input — and every
prior `get_workflow_package_draft` / `get_workflow_package` result —
stayed in the transcript verbatim, multi-turn authoring sessions would
burn the entire context window on duplicate payload. To avoid that, the
runtime keeps **at most one** full workflow-package payload in your
transcript at a time. Older carriers (whether they were emissions or
fetch results) get demoted to a small redaction stub.

## What the stub looks like

When you scan a prior turn and see something like:

```json
{
  "package": {
    "_redacted": true,
    "sha256": "9f86d081…",
    "sizeBytes": 184320,
    "summary": {
      "workflowCount": 1,
      "nodeCount": 6,
      "agentCount": 3,
      "roleCount": 1,
      "entryPoint": { "key": "demo-pipeline", "version": 1 }
    }
  }
}
```

…that is the runtime's bookkeeping. The shape is identical whether it
appears in a `tool_use` Input (a prior emission you made) or in a
`tool_result` body (a prior fetch). The `summary` block is for your
benefit so you can still reason about what's in the draft ("I authored a
6-node pipeline with 3 agents") without re-receiving the bytes.

## Do NOT copy the stub

The placeholder has the same outer shape as a real `package` argument
(JSON object), so it is structurally tempting to feed it back. **Don't.**
Both `set_workflow_package_draft` and `save_workflow_package` reject the
stub at tool entry — they look for `_redacted: true` and return an error
without writing anything. Calling them with the stub costs the user a
wasted round-trip and produces zero forward progress.

If the model's loop sees an error message like *"The `package` argument
is a redaction placeholder, not a real workflow package"*, that error is
the rejection path — re-read this skill and use one of the routes below.

## How to act on the current state

The redaction is **transcript-only**. The actual draft is on disk in the
conversation's workspace, untouched. The right access pattern depends on
what you're trying to do:

- **Inspect what's in the draft.** Call `get_workflow_package_draft()`.
  The current state's full bytes will surface as the most recent
  carrier in your transcript, and the runtime will demote any prior
  full payload (including a prior fetch result) to the stub. After this
  call you have one fresh full copy to read; act on it within the same
  turn.
- **Edit the draft.** Call `patch_workflow_package_draft({ operations:
  [...] })` with RFC 6902 ops. Patching happens server-side against the
  on-disk draft — you never need to re-emit the package to make small
  edits (add an edge, swap a port name, tweak `maxRoundsPerRound`).
- **Save the draft.** Call `save_workflow_package` with **no
  arguments**. The tool reads the on-disk draft directly. Don't pass
  `package` at all on the draft path.
- **Inspect a library workflow.** `get_workflow_package({ key,
  version })` is also a redaction carrier — the most recent fetch is
  full, older fetches are demoted to the stub.

## Why this matters

A real authoring session that breaches the N=1 buffer (e.g., calling
`get_workflow_package_draft` twice within a turn before reading the
first result) will see the older fetch demoted to the stub. That is
working as intended — the stub still tells you *"this turn fetched a
draft of N nodes"* even when the bytes are gone. If you actually need
the bytes again, fetch again; the fetch is cheap.
