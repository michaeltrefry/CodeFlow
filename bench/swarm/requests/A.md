# Request A — PRD from a sparse brief

> Used by the swarm-bench harness ([sc-41](https://app.shortcut.com/trefry/story/41) / `docs/swarm-bench-harness.md`). One of two synthesis-shaped requests. Paste the **brief** below into the variant workflow's input field; the variant's answer is the PRD.

## Brief

We want a workflow node type that, given a Shortcut story or epic ID, pulls the title and description and seeds them into the workflow as `workflow.mission` and `workflow.context`. The node sits early in a workflow alongside Start, so the rest of the workflow can read both keys without the operator pasting story content into the input field by hand. Should work for both stories and epics, and degrade clearly when the ID can't be resolved.

## Task for the agent (or panel)

Produce a Product Requirements Document for this capability. The PRD should include — at minimum — sections for:

- **Problem** — what's painful today; who feels the pain; how often.
- **Users** — who initiates this node; who reads its output downstream.
- **Goals** — what success looks like, ideally measurable.
- **Non-goals** — what we are explicitly NOT shipping in v1.
- **Acceptance criteria** — observable behaviour that gates "done."
- **Open questions** — design decisions that need to be made before implementation; flag the ones that block a v1.
- **Dependencies** — other CodeFlow capabilities, configuration, or external services this depends on.

Aim for ~600–1500 words. No headings beyond the seven above unless they materially help.
