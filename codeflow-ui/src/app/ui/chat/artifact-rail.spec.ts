import { COLLAPSE_THRESHOLD, filterRailEvents, formatArtifactAge } from './artifact-rail.component';
import { ArtifactEventView } from './chat-panel.component';

/**
 * sc-796 (AA-5) — pure-function tests for the rail's filter + age formatter. Keep these
 * deterministic + cheap so they run on every UI build; the component-mount path is
 * exercised through the broader chat-panel integration tests.
 */

function makeView(overrides: Partial<ArtifactEventView> = {}): ArtifactEventView {
  return {
    id: overrides.id ?? '00000000-0000-0000-0000-000000000001',
    conversationId: 'conv-1',
    sequence: overrides.sequence ?? 1,
    artifactKind: overrides.artifactKind ?? 'WorkflowPackageDraft',
    name: overrides.name ?? 'draft.cf-workflow-package.json',
    snapshotId: overrides.snapshotId ?? null,
    summary: overrides.summary ?? null,
    createdAtUtc: overrides.createdAtUtc ?? '2026-05-06T12:00:00Z',
    superseded: overrides.superseded ?? false,
    expired: overrides.expired ?? false,
  };
}

describe('filterRailEvents (AA-5 rail visibility)', () => {
  it('hides superseded entries by default', () => {
    const result = filterRailEvents([
      makeView({ id: 'a', sequence: 1, superseded: true }),
      makeView({ id: 'b', sequence: 2, superseded: false }),
    ], false);

    expect(result.map(e => e.id)).toEqual(['b']);
  });

  it('reveals superseded entries when toggled on', () => {
    const result = filterRailEvents([
      makeView({ id: 'a', sequence: 1, superseded: true }),
      makeView({ id: 'b', sequence: 2, superseded: false }),
    ], true);

    // Newest-first ordering must hold even with superseded shown.
    expect(result.map(e => e.id)).toEqual(['b', 'a']);
  });

  it('orders by sequence newest-first', () => {
    // Insertion order matches the live-append code; the rail must render top-down newest first
    // regardless of how the chat panel happened to populate the array.
    const result = filterRailEvents([
      makeView({ id: 'a', sequence: 1 }),
      makeView({ id: 'b', sequence: 3 }),
      makeView({ id: 'c', sequence: 2 }),
    ], false);

    expect(result.map(e => e.id)).toEqual(['b', 'c', 'a']);
  });

  it('keeps expired entries (still actionable as Download? no — but visible as tombstones)', () => {
    // Expired ≠ superseded. Expired rows still show in the rail with their actions swapped to
    // an "Expired" status — they're surfaced for lineage/audit. Only superseded gets filtered.
    const result = filterRailEvents([
      makeView({ id: 'a', sequence: 1, expired: true }),
      makeView({ id: 'b', sequence: 2, expired: false }),
    ], false);

    expect(result.map(e => e.id)).toEqual(['b', 'a']);
  });

  it('does not mutate the input array', () => {
    const input = [
      makeView({ id: 'a', sequence: 1 }),
      makeView({ id: 'b', sequence: 2 }),
    ];
    const beforeIds = input.map(e => e.id);

    filterRailEvents(input, false);

    // Input order is preserved; the helper sorts a copy.
    expect(input.map(e => e.id)).toEqual(beforeIds);
  });
});

describe('formatArtifactAge (AA-5 rail relative time)', () => {
  const created = '2026-05-06T12:00:00Z';
  const createdMs = Date.parse(created);

  it('returns "just now" within the first minute', () => {
    expect(formatArtifactAge(created, createdMs + 30_000)).toBe('just now');
    expect(formatArtifactAge(created, createdMs + 59_999)).toBe('just now');
  });

  it('returns minutes between 1m and 59m', () => {
    expect(formatArtifactAge(created, createdMs + 60_000)).toBe('1m');
    expect(formatArtifactAge(created, createdMs + 5 * 60_000)).toBe('5m');
    expect(formatArtifactAge(created, createdMs + 59 * 60_000)).toBe('59m');
  });

  it('returns hours between 1h and 23h', () => {
    expect(formatArtifactAge(created, createdMs + 60 * 60_000)).toBe('1h');
    expect(formatArtifactAge(created, createdMs + 5 * 60 * 60_000)).toBe('5h');
    expect(formatArtifactAge(created, createdMs + 23 * 60 * 60_000)).toBe('23h');
  });

  it('returns days from 1d onward', () => {
    expect(formatArtifactAge(created, createdMs + 24 * 60 * 60_000)).toBe('1d');
    expect(formatArtifactAge(created, createdMs + 7 * 24 * 60 * 60_000)).toBe('7d');
  });

  it('clamps a future-dated event to "just now"', () => {
    // Clock skew between server and client could produce a `nowMs < createdMs` situation.
    // Don't render a negative — collapse to "just now" so the rail stays sane.
    expect(formatArtifactAge(created, createdMs - 60_000)).toBe('just now');
  });

  it('returns empty string for an unparseable timestamp', () => {
    expect(formatArtifactAge('not-a-date', Date.now())).toBe('');
  });
});

describe('COLLAPSE_THRESHOLD (AA-5)', () => {
  it('is exported as a stable number for the chat-panel integration', () => {
    // Pin the contract — changing it without updating the rail's collapsed-state UX would
    // surprise users who saw the rail expanded yesterday.
    expect(COLLAPSE_THRESHOLD).toBe(4);
  });
});
