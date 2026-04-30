import type { MonacoAmbientLib } from './monaco-script-editor.component';

/** E1: compose Monaco ambient declarations for a script-editor slot. The active set is
 *  swapped into Monaco's TS service when an editor takes focus. Symbol availability is
 *  gated by script kind so `output` is undefined inside an input script (and vice versa),
 *  matching runtime semantics. F2 dataflow narrows `workflow` / `context` to known keys
 *  while keeping an index signature so unknown keys don't error. */
export type ScriptSlotKind = 'input-script' | 'output-script' | 'logic-script';

export function buildScriptAmbientLibs(
  kind: ScriptSlotKind,
  workflowKeys: readonly string[],
  contextKeys: readonly string[],
  inLoop: boolean
): MonacoAmbientLib[] {
  const wfNarrow = workflowKeys.length === 0
    ? '[key: string]: unknown;'
    : workflowKeys.map(k => `${JSON.stringify(k)}?: unknown;`).join(' ') + ' [key: string]: unknown;';
  const ctxNarrow = contextKeys.length === 0
    ? '[key: string]: unknown;'
    : contextKeys.map(k => `${JSON.stringify(k)}?: unknown;`).join(' ') + ' [key: string]: unknown;';

  const sharedHeader = [
    '// CodeFlow script sandbox - auto-generated ambient declarations.',
    '// Do not edit; regenerated when this script-editor takes focus.',
    `declare const workflow: { ${wfNarrow} };`,
    `declare const context: { ${ctxNarrow} };`,
    'declare function setWorkflow(key: string, value: unknown): void;',
    'declare function setContext(key: string, value: unknown): void;',
    'declare function log(message: string): void;'
  ];

  const loopBlock = inLoop
    ? [
        'declare const round: number;',
        'declare const maxRounds: number;',
        'declare const isLastRound: boolean;'
      ]
    : [];

  const slotBlock = kind === 'input-script'
    ? [
        '// Input scripts run BEFORE the node receives the upstream artifact.',
        'declare const input: unknown;',
        'declare function setInput(text: string): void;'
      ]
    : kind === 'output-script'
    ? [
        '// Output scripts run AFTER the agent submits.',
        'declare const output: { decision: string; text: string };',
        'declare function setOutput(text: string): void;',
        'declare function setNodePath(port: string): void;'
      ]
    : [
        '// Logic-node scripts evaluate the node\'s decision against the upstream artifact.',
        'declare const output: { decision: string; text: string };',
        'declare function setOutput(text: string): void;',
        'declare function setNodePath(port: string): void;'
      ];

  const content = [...sharedHeader, ...loopBlock, ...slotBlock, ''].join('\n');
  return [{ filePath: `inmemory://codeflow/${kind}.d.ts`, content }];
}
