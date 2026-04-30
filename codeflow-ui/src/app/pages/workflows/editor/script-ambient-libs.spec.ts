import { describe, expect, it } from 'vitest';
import { buildScriptAmbientLibs } from './script-ambient-libs';

describe('buildScriptAmbientLibs', () => {
  it('narrows workflow and context keys while retaining an index signature', () => {
    const [lib] = buildScriptAmbientLibs('input-script', ['customerId'], ['trace id'], false);

    expect(lib.filePath).toBe('inmemory://codeflow/input-script.d.ts');
    expect(lib.content).toContain('"customerId"?: unknown; [key: string]: unknown;');
    expect(lib.content).toContain('"trace id"?: unknown; [key: string]: unknown;');
    expect(lib.content).toContain('declare const input: unknown;');
    expect(lib.content).not.toContain('declare const output:');
  });

  it('adds loop bindings and output helpers for output scripts', () => {
    const [lib] = buildScriptAmbientLibs('output-script', [], [], true);

    expect(lib.content).toContain('declare const round: number;');
    expect(lib.content).toContain('declare const maxRounds: number;');
    expect(lib.content).toContain('declare const isLastRound: boolean;');
    expect(lib.content).toContain('declare const output: { decision: string; text: string };');
    expect(lib.content).toContain('declare function setNodePath(port: string): void;');
  });
});
