#!/usr/bin/env python3
"""Rewrite starter workflow packages for the user-defined port model.

For each package file:
  - Strip 'Failed' from every workflow node's `outputPorts` (it's implicit on every node).
  - Strip 'Exhausted' from every workflow node's `outputPorts` (reserved for ReviewLoop synthesis).
  - Strip 'Failed' from every agent's declared `outputs` (the FailTool path handles failures
    out-of-band; declaring 'Failed' as an LLM-selectable decision is no longer correct).
  - Drop edges whose `fromPort` was 'Failed' or 'Exhausted' on a non-ReviewLoop source — keep
    them as wirable handles in the editor; the implicit Failed port is renderable but its old
    in-package wiring becomes unnecessary unless re-authored.

This script is a one-shot: after rewrite the JSON should re-import cleanly under the new
validator. Re-run is idempotent.
"""

import json
import sys
from pathlib import Path

PACKAGES = [
    "artifact-review-loop-v2-package.json",
    "product-requirements-v8-package.json",
    "socratic-interview-v15-package.json",
]


def strip_implicit(ports, *, node_kind):
    if not isinstance(ports, list):
        return ports
    out = []
    for p in ports:
        if p == "Failed":
            continue
        if p == "Exhausted":
            # Synthesized on ReviewLoop. On any other kind it's reserved/illegal.
            # Even on ReviewLoop, the editor synthesizes it — never declared in JSON.
            continue
        out.append(p)
    return out


def rewrite_workflow(workflow):
    nodes = workflow.get("nodes", []) or []
    for node in nodes:
        ports = node.get("outputPorts")
        if isinstance(ports, list):
            node["outputPorts"] = strip_implicit(ports, node_kind=node.get("kind"))
    edges = workflow.get("edges", []) or []
    return workflow


def rewrite_agent(agent):
    config = agent.get("config")
    if not isinstance(config, dict):
        return agent
    outputs = config.get("outputs")
    if isinstance(outputs, list):
        config["outputs"] = [o for o in outputs if not (isinstance(o, dict) and o.get("kind") == "Failed")]
    return agent


def rewrite_package(path: Path) -> bool:
    data = json.loads(path.read_text())
    workflows = data.get("workflows", []) or []
    for wf in workflows:
        rewrite_workflow(wf)
    agents = data.get("agents", []) or []
    for agent in agents:
        rewrite_agent(agent)
    serialised = json.dumps(data, indent=2, ensure_ascii=False) + "\n"
    if path.read_text() == serialised:
        return False
    path.write_text(serialised)
    return True


def main():
    here = Path(__file__).resolve().parent
    any_changed = False
    for name in PACKAGES:
        path = here / name
        if not path.exists():
            print(f"[skip] {name} (missing)")
            continue
        changed = rewrite_package(path)
        any_changed = any_changed or changed
        print(f"[{'edit' if changed else 'noop'}] {name}")
    return 0 if any_changed else 0


if __name__ == "__main__":
    sys.exit(main())
