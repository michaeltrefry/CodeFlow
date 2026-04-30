import { verdictSourceBadge } from './trace-timeline.types';

describe('verdictSourceBadge', () => {
  it('returns a warn-tinted "mechanical" chip for mechanical-gate decisions', () => {
    const badge = verdictSourceBadge('mechanical');
    expect(badge).not.toBeNull();
    expect(badge!.label).toBe('mechanical');
    expect(badge!.variant).toBe('warn');
    expect(badge!.title).toContain('mechanical gate');
    expect(badge!.title).toContain('run_command');
  });

  it('returns an accent-tinted "model" chip for LLM-only agent decisions', () => {
    const badge = verdictSourceBadge('model');
    expect(badge).not.toBeNull();
    expect(badge!.label).toBe('model');
    expect(badge!.variant).toBe('accent');
    expect(badge!.title).toContain('LLM-only');
  });

  it.each([
    null,
    undefined,
  ])('returns null when verdict source is %s — UI omits the chip', (input) => {
    // Mixed grant sets (e.g. read_file-only inspectors) leave the source nullable so the
    // timeline doesn't claim either bucket. The trace-detail mapping guards on null
    // before pushing the chip into badges.
    expect(verdictSourceBadge(input as 'mechanical' | 'model' | null | undefined)).toBeNull();
  });
});
