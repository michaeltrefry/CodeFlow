/**
 * E2 — Script snippet library shipped to the Monaco editor.
 *
 * Each snippet is keyed to one or more script slots (`input-script` /
 * `output-script` / `logic-script`) so callers only see snippets whose
 * generated code compiles cleanly under E1's ambient typings for that slot.
 *
 * Several entries are explicitly marked LEGACY where Phase 3's built-in
 * features (P3 rejection-history, P4 mirror-output, P5 replace-from-workflow-var)
 * absorb the same pattern declaratively. The legacy hint surfaces in the
 * suggestion's documentation so authors learn the modern path while still
 * having the snippet for edge cases.
 *
 * Versioned. Bump the SCRIPT_SNIPPET_LIBRARY_VERSION when changing any
 * snippet body so consumers can detect upgrades.
 */
export const SCRIPT_SNIPPET_LIBRARY_VERSION = 2;

export type SnippetKind = 'input-script' | 'output-script' | 'logic-script';

export interface ScriptSnippet {
  /** Stable id; kept across renames. Useful for telemetry / dedupe. */
  readonly id: string;
  /** What appears in the autocomplete list. Prefix with `cf:` so authors recognize CodeFlow snippets. */
  readonly label: string;
  /** Right-side detail badge in the suggestion list. */
  readonly detail: string;
  /** Markdown body shown when the suggestion is highlighted. */
  readonly documentation: string;
  /** Snippet body with `${1:...}` tabstops per Monaco snippet syntax. */
  readonly insertText: string;
  /** Slots this snippet should appear in. Gated to slots whose ambient typings include all referenced symbols. */
  readonly kinds: readonly SnippetKind[];
  /** True when a Phase 3 built-in feature supersedes this snippet for the common case. */
  readonly legacy?: boolean;
  /** When true, only offered inside ReviewLoop children where `round` / `maxRounds` / `isLastRound` are bound. */
  readonly requiresLoop?: boolean;
}

/** Resolves a snippet against an editor's slot context. */
export interface SnippetContext {
  readonly kind: SnippetKind;
  readonly inLoop: boolean;
}

const ALL_KINDS: readonly SnippetKind[] = ['input-script', 'output-script', 'logic-script'];
const OUTPUT_KINDS: readonly SnippetKind[] = ['output-script', 'logic-script'];

const LEGACY_BANNER = '\n\n> **Legacy.** Prefer the built-in feature where possible — see the linked node-config option below.';

export const SCRIPT_SNIPPETS: readonly ScriptSnippet[] = [
  {
    id: 'capture-output-to-workflow-var',
    label: 'cf:mirror-output-to-workflow-var',
    detail: 'Pattern-1 (legacy)',
    documentation:
      'Copy this agent\'s output text into a workflow variable so a downstream node can ' +
      'read it.\n\n**P4 supersedes:** the *Mirror output to workflow variable* checkbox on ' +
      'the Agent node runs *before* the output script and is preferred for the common case.' +
      LEGACY_BANNER,
    insertText: [
      "// Pattern-1 (legacy) — prefer the \"Mirror output to workflow variable\" Agent-node config (P4) for the common case.",
      "setWorkflow('${1:variableName}', output.text);"
    ].join('\n'),
    kinds: OUTPUT_KINDS,
    legacy: true,
  },
  {
    id: 'replace-output-from-workflow-var',
    label: 'cf:replace-artifact-from-workflow-var',
    detail: 'Pattern-2 (legacy)',
    documentation:
      'Override the artifact flowing downstream when this agent picks a specific port, ' +
      'pulling the replacement from a workflow variable.\n\n**P5 supersedes:** the per-port ' +
      '*Replace artifact from workflow variable* config binds at the port level, runs *after* ' +
      'the script, and takes precedence over `setOutput()`.' + LEGACY_BANNER,
    insertText: [
      "// Pattern-2 (legacy) — prefer the per-port \"Replace artifact from workflow variable\" Agent-node config (P5) for the common case.",
      "if (output.decision === '${1:Approved}') {",
      "  const replacement = workflow.${2:variableName};",
      "  if (typeof replacement === 'string' && replacement.length > 0) {",
      "    setOutput(replacement);",
      "  }",
      "}"
    ].join('\n'),
    kinds: OUTPUT_KINDS,
    legacy: true,
  },
  {
    id: 'accumulate-rejection-history',
    label: 'cf:accumulate-rejection-history',
    detail: 'ReviewLoop (legacy)',
    documentation:
      'Append the reviewer\'s rejection text into a workflow variable each round so ' +
      'producers on later rounds can see prior feedback.\n\n**P3 supersedes:** turn on ' +
      '*Rejection history* in the ReviewLoop node config — the runtime accumulates into ' +
      '`__loop.rejectionHistory` and exposes `{{ rejectionHistory }}` to children automatically.' +
      LEGACY_BANNER,
    insertText: [
      "// Legacy — prefer ReviewLoop's built-in Rejection history config (P3) for the common case.",
      "if (output.decision === 'Rejected') {",
      "  const previous = (typeof workflow.${1:rejectionHistory} === 'string') ? workflow.${1:rejectionHistory} : '';",
      "  const entry = '## Round ' + round + '\\n' + output.text;",
      "  setWorkflow('${1:rejectionHistory}', previous ? previous + '\\n\\n' + entry : entry);",
      "}"
    ].join('\n'),
    kinds: OUTPUT_KINDS,
    legacy: true,
    requiresLoop: true,
  },
  {
    id: 'increment-counter',
    label: 'cf:increment-counter',
    detail: 'Counter',
    documentation:
      'Increment a workflow-variable counter. Useful for tracking off-loop iteration counts ' +
      'or tagging artifacts with a sequence number.',
    insertText: [
      "const ${1:count} = (typeof workflow.${2:counterName} === 'number' ? workflow.${2:counterName} : 0) + 1;",
      "setWorkflow('${2:counterName}', ${1:count});"
    ].join('\n'),
    kinds: ALL_KINDS,
  },
  {
    id: 'branch-on-workflow-state',
    label: 'cf:branch-on-workflow-state',
    detail: 'Routing',
    documentation:
      'Route this node\'s output to a different port based on a workflow variable\'s value. ' +
      'The chosen port name must match one of this node\'s wired output ports.',
    insertText: [
      "if (workflow.${1:stateKey} === '${2:expectedValue}') {",
      "  setNodePath('${3:ApprovedPort}');",
      "} else {",
      "  setNodePath('${4:OtherPort}');",
      "}"
    ].join('\n'),
    kinds: OUTPUT_KINDS,
  },
  {
    id: 'seed-workflow-from-input',
    label: 'cf:seed-workflow-from-input',
    detail: 'Input',
    documentation:
      'Pull a value out of the upstream artifact and stash it as a workflow variable so ' +
      'later nodes can read it without re-parsing the input.',
    insertText: [
      "// Seed a workflow variable from the upstream artifact before this node runs.",
      "// Adjust the source field to match the artifact shape this node receives.",
      "if (typeof input === 'object' && input !== null) {",
      "  const source = /** @type {Record<string, unknown>} */ (input);",
      "  setWorkflow('${1:variableName}', source['${2:fieldName}']);",
      "}"
    ].join('\n'),
    kinds: ['input-script'],
  },
  {
    id: 'rewrite-input-artifact',
    label: 'cf:rewrite-input-artifact',
    detail: 'Input',
    documentation:
      'Replace the artifact this node receives. The new text becomes `input` for the agent ' +
      'and any downstream consumer that reads the node\'s incoming artifact.',
    insertText: [
      "setInput('${1:replacement text}');"
    ].join('\n'),
    kinds: ['input-script'],
  },
  {
    id: 'boundary-input-from-workflow-var',
    label: 'cf:boundary-input-from-workflow-var',
    detail: 'Subflow / ReviewLoop',
    documentation:
      'Boundary input script: build the child saga\'s seed artifact from a workflow variable. ' +
      'Runs once before the child workflow is dispatched (for ReviewLoop, only before round 1). ' +
      'Use this when a Subflow needs to operate on something accumulated upstream rather than ' +
      'whatever artifact happens to be on the incoming edge.',
    insertText: [
      "// Boundary input — runs once before the child saga starts.",
      "const seed = workflow.${1:variableName};",
      "if (typeof seed === 'string' && seed.length > 0) {",
      "  setInput(seed);",
      "}"
    ].join('\n'),
    kinds: ['input-script'],
  },
  {
    id: 'boundary-output-route-on-terminal',
    label: 'cf:boundary-output-route-on-terminal',
    detail: 'Subflow / ReviewLoop',
    documentation:
      'Boundary output script: choose the parent\'s outgoing port based on the child\'s ' +
      'terminal port. `output.decision` is the child\'s terminal port name (or, for ReviewLoop, ' +
      'whichever port closed the loop — including the synthesized `Exhausted`). The chosen ' +
      'port must match one of the boundary node\'s wired ports plus the implicit `Failed`.',
    insertText: [
      "// Boundary output — runs once after the child terminates.",
      "// output.decision = child's terminal port (or 'Exhausted' on a budget-exhausted ReviewLoop).",
      "if (output.decision === '${1:Approved}') {",
      "  setNodePath('${2:Continue}');",
      "} else if (output.decision === 'Exhausted') {",
      "  setNodePath('${3:Escalate}');",
      "} else {",
      "  setNodePath('Failed');",
      "}"
    ].join('\n'),
    kinds: ['output-script'],
  },
  {
    id: 'boundary-output-summarize-loop-exit',
    label: 'cf:boundary-output-summarize-loop-exit',
    detail: 'ReviewLoop',
    documentation:
      'ReviewLoop boundary output: stash a summary of how the loop ended into the workflow ' +
      'bag so downstream nodes can render or branch on it. Fires once per loop activation, ' +
      'never per iteration.',
    insertText: [
      "// Boundary output on ReviewLoop — fires once after the loop exits.",
      "const exited = output.decision === 'Exhausted' ? 'budget-exhausted' : 'approved';",
      "setWorkflow('${1:loopOutcome}', exited);"
    ].join('\n'),
    kinds: ['output-script'],
  },
];

/** Returns the snippet subset whose generated code compiles in the given script slot. */
export function getSnippetsForKind(kind: SnippetKind): readonly ScriptSnippet[] {
  return SCRIPT_SNIPPETS.filter(s => s.kinds.includes(kind));
}

/** Returns the snippet subset that compiles cleanly in the given editor context. */
export function getSnippetsForContext(ctx: SnippetContext): readonly ScriptSnippet[] {
  return SCRIPT_SNIPPETS.filter(s =>
    s.kinds.includes(ctx.kind) && (!s.requiresLoop || ctx.inLoop)
  );
}
