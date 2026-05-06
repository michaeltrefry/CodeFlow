import { findPriorArtifactByName } from './artifact-diff.component';
import { ArtifactEventView, parseEntryPointFromSummary } from './chat-panel.component';

/**
 * sc-798 (AA-7) — pure-function tests for the diff viewer's input-resolution helpers. The
 * Monaco-mounting component itself runs through the broader chat-panel integration paths;
 * we keep the helper tests deterministic + cheap so they run on every UI build.
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

describe('findPriorArtifactByName (AA-7 prior-version resolution)', () => {
  it('returns the immediately-superseded same-name event', () => {
    // Common case: Set draft → patch → patch. The newest is active; the two prior drafts
    // are both superseded; we want the most-recent of the two as the diff baseline.
    const events = [
      makeView({ id: 'd1', sequence: 1, name: 'draft', superseded: true }),
      makeView({ id: 'd2', sequence: 2, name: 'draft', superseded: true }),
      makeView({ id: 'd3', sequence: 3, name: 'draft', superseded: false }),
    ];

    const prior = findPriorArtifactByName(events, events[2]);

    expect(prior?.id).toBe('d2');
  });

  it('returns null when no prior version exists', () => {
    // First event for a name. Diff viewer's Prior tab should gray out.
    const events = [
      makeView({ id: 'd1', sequence: 1, name: 'draft', superseded: false }),
    ];

    const prior = findPriorArtifactByName(events, events[0]);

    expect(prior).toBeNull();
  });

  it('ignores events with a different name', () => {
    // A snapshot in the same conversation must not be picked as the "prior draft" — they're
    // independent artifacts. Snapshot kinds are unique-by-id and never share a name.
    const events = [
      makeView({ id: 's1', sequence: 1, name: 'snapshot-abc', artifactKind: 'WorkflowPackageSnapshot', superseded: false }),
      makeView({ id: 'd1', sequence: 2, name: 'draft', superseded: false }),
    ];

    const prior = findPriorArtifactByName(events, events[1]);

    expect(prior).toBeNull();
  });

  it('ignores events at or after the current sequence', () => {
    // Two active drafts under the same name shouldn't happen (supersession is mutually
    // exclusive), but if it did, we still only consider strictly earlier events.
    const events = [
      makeView({ id: 'd1', sequence: 5, name: 'draft', superseded: false }),
      makeView({ id: 'd2', sequence: 7, name: 'draft', superseded: false }),
    ];

    const prior = findPriorArtifactByName(events, events[0]);

    expect(prior).toBeNull();
  });

  it('ignores active (non-superseded) prior events', () => {
    // Active prior with same name = an inconsistency the recorder shouldn't have created.
    // We refuse to treat it as a diff baseline so we don't reinforce bad state.
    const events = [
      makeView({ id: 'd1', sequence: 1, name: 'draft', superseded: false }),
      makeView({ id: 'd2', sequence: 2, name: 'draft', superseded: false }),
    ];

    const prior = findPriorArtifactByName(events, events[1]);

    expect(prior).toBeNull();
  });

  it('returns the highest-sequence superseded event when several exist', () => {
    // Order of items in the input array shouldn't matter — picker selects by sequence, not
    // array position.
    const events = [
      makeView({ id: 'd2', sequence: 2, name: 'draft', superseded: true }),
      makeView({ id: 'd1', sequence: 1, name: 'draft', superseded: true }),
      makeView({ id: 'd5', sequence: 5, name: 'draft', superseded: false }),
      makeView({ id: 'd3', sequence: 3, name: 'draft', superseded: true }),
    ];

    const prior = findPriorArtifactByName(events, events[2]);

    expect(prior?.id).toBe('d3');
  });
});

describe('parseEntryPointFromSummary (AA-7 library-mode input)', () => {
  it('extracts entry-point key + version from the canonical summary shape', () => {
    const result = parseEntryPointFromSummary(JSON.stringify({
      entryPoint: { key: 'demo-flow', version: 3 },
      workflows: [],
    }));

    expect(result).toEqual({ key: 'demo-flow', version: 3 });
  });

  it('returns the key with null version when version is absent', () => {
    // Some package summaries omit the version (e.g. an early draft before the user picked
    // a version). Library mode then fetches the latest version of the key.
    const result = parseEntryPointFromSummary(JSON.stringify({
      entryPoint: { key: 'demo-flow' },
    }));

    expect(result).toEqual({ key: 'demo-flow', version: null });
  });

  it('returns null when no summary is provided', () => {
    expect(parseEntryPointFromSummary(null)).toBeNull();
  });

  it('returns null when the summary is malformed JSON', () => {
    expect(parseEntryPointFromSummary('not json')).toBeNull();
  });

  it('returns null when entryPoint.key is missing or not a string', () => {
    expect(parseEntryPointFromSummary(JSON.stringify({}))).toBeNull();
    expect(parseEntryPointFromSummary(JSON.stringify({ entryPoint: {} }))).toBeNull();
    expect(parseEntryPointFromSummary(JSON.stringify({ entryPoint: { key: 0 } }))).toBeNull();
  });
});
