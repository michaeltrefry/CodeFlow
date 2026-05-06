import { applyArtifactEvent, ArtifactEventView } from './chat-panel.component';
import { ArtifactEvent, parseSseFrame } from '../../core/assistant-stream';

/**
 * sc-793 (AA-2) — pure-function tests for the artifact-event live append + supersession
 * logic. These cover the at-least-once dedupe path, the supersede-by-name semantics that
 * mirror the recorder's repository-side update, and the SSE parser frame for the new
 * `artifact-event` event type.
 *
 * The full live-render path (signal updates flowing into the thread) is exercised end-to-end
 * by the existing chat-panel integration tests in CodeFlow.Api.Tests; here we only assert
 * the helper-level invariants since they're cheap and deterministic.
 */

function makeEvent(overrides: Partial<ArtifactEvent> = {}): ArtifactEvent {
  return {
    id: overrides.id ?? '00000000-0000-0000-0000-000000000001',
    conversationId: 'conv-1',
    sequence: overrides.sequence ?? 1,
    kind: overrides.kind ?? 'WorkflowPackageDraft',
    name: overrides.name ?? 'draft.cf-workflow-package.json',
    snapshotId: overrides.snapshotId ?? null,
    summary: overrides.summary ?? null,
    createdAtUtc: overrides.createdAtUtc ?? '2026-05-06T12:00:00Z',
  };
}

describe('applyArtifactEvent (AA-2 supersession + dedupe)', () => {
  it('appends a fresh event onto an empty list', () => {
    const result = applyArtifactEvent([], makeEvent({ id: 'a', sequence: 1 }), false);

    expect(result).toHaveLength(1);
    expect(result[0].id).toBe('a');
    expect(result[0].superseded).toBe(false);
    expect(result[0].expired).toBe(false);
  });

  it('is idempotent — re-arrival of the same id leaves the list unchanged', () => {
    const first = applyArtifactEvent([], makeEvent({ id: 'a' }), false);
    const reference = first;
    const second = applyArtifactEvent(first, makeEvent({ id: 'a' }), false);

    expect(second).toBe(reference);
    expect(second).toHaveLength(1);
  });

  it('supersedesPriorByName=true marks earlier same-name actives as superseded', () => {
    // Common scenario: two patch_workflow_package_draft calls on the same draft. The second
    // event must mark the first as superseded; both rows stay in the list so the UI can show
    // lineage (muted prior) but only the active one drives any actions.
    const afterFirst = applyArtifactEvent(
      [],
      makeEvent({ id: 'a', sequence: 1, name: 'draft' }),
      true);
    const afterSecond = applyArtifactEvent(
      afterFirst,
      makeEvent({ id: 'b', sequence: 2, name: 'draft' }),
      true);

    expect(afterSecond).toHaveLength(2);
    const byId = new Map(afterSecond.map(e => [e.id, e]));
    expect(byId.get('a')!.superseded).toBe(true);
    expect(byId.get('b')!.superseded).toBe(false);
  });

  it('supersedesPriorByName=true does not touch events with a different name', () => {
    // A draft and a snapshot coexist for the same conversation. A second draft event must
    // supersede the prior draft but leave the snapshot alone — they're independent artifacts.
    const seeded = applyArtifactEvent(
      applyArtifactEvent([], makeEvent({ id: 'd1', name: 'draft' }), true),
      makeEvent({ id: 's1', name: 'snapshot-abc.cf-workflow-package.json', kind: 'WorkflowPackageSnapshot' }),
      false);

    const after = applyArtifactEvent(seeded, makeEvent({ id: 'd2', name: 'draft' }), true);

    const byId = new Map(after.map(e => [e.id, e]));
    expect(byId.get('d1')!.superseded).toBe(true);
    expect(byId.get('s1')!.superseded).toBe(false);
    expect(byId.get('d2')!.superseded).toBe(false);
  });

  it('supersedesPriorByName=false leaves prior events alone (snapshot path)', () => {
    // Snapshots accrue without superseding each other — multiple per-save snapshots can
    // coexist in a single conversation. The flag must be honored faithfully.
    const seeded = applyArtifactEvent(
      [],
      makeEvent({ id: 's1', name: 'snapshot-1', kind: 'WorkflowPackageSnapshot' }),
      false);

    const after = applyArtifactEvent(seeded, makeEvent({ id: 's2', name: 'snapshot-2', kind: 'WorkflowPackageSnapshot' }), false);

    expect(after.find(e => e.id === 's1')!.superseded).toBe(false);
    expect(after.find(e => e.id === 's2')!.superseded).toBe(false);
  });

  it('does not re-flip an already-superseded event when a third event lands', () => {
    // Three drafts in a row: d1 superseded by d2, d2 superseded by d3. d1 stays superseded;
    // it must not flicker because the predicate filters on `!p.superseded`.
    let list: ArtifactEventView[] = [];
    list = applyArtifactEvent(list, makeEvent({ id: 'd1', name: 'draft' }), true);
    list = applyArtifactEvent(list, makeEvent({ id: 'd2', name: 'draft' }), true);
    list = applyArtifactEvent(list, makeEvent({ id: 'd3', name: 'draft' }), true);

    const byId = new Map(list.map(e => [e.id, e]));
    expect(byId.get('d1')!.superseded).toBe(true);
    expect(byId.get('d2')!.superseded).toBe(true);
    expect(byId.get('d3')!.superseded).toBe(false);
  });
});

describe('parseSseFrame artifact-event handling', () => {
  it('hydrates the new artifact-event SSE frame into the typed event shape', () => {
    const payload = {
      id: 'event-1',
      conversationId: 'conv-1',
      sequence: 3,
      kind: 'WorkflowPackageDraft',
      name: 'draft.cf-workflow-package.json',
      snapshotId: null,
      summary: '{"entryPoint":{"key":"demo","version":1}}',
      createdAtUtc: '2026-05-06T12:00:00Z',
      supersedesPriorByName: true,
    };

    const result = parseSseFrame({
      eventName: 'artifact-event',
      dataLines: [JSON.stringify(payload)],
    });

    expect(result).toEqual({
      kind: 'artifact-event',
      event: {
        id: 'event-1',
        conversationId: 'conv-1',
        sequence: 3,
        kind: 'WorkflowPackageDraft',
        name: 'draft.cf-workflow-package.json',
        snapshotId: null,
        summary: '{"entryPoint":{"key":"demo","version":1}}',
        createdAtUtc: '2026-05-06T12:00:00Z',
      },
      supersedesPriorByName: true,
    });
  });

  it('defaults supersedesPriorByName to false when the server omits it', () => {
    // Defensive shape — older servers (before AA-2) won't emit the flag. The frame should
    // still parse, with `supersedesPriorByName: false` so prior pills aren't accidentally
    // marked stale.
    const payload = {
      id: 'event-1',
      conversationId: 'conv-1',
      sequence: 1,
      kind: 'WorkflowPackageSnapshot',
      name: 'snapshot.cf-workflow-package.json',
      snapshotId: 'snap-1',
      summary: null,
      createdAtUtc: '2026-05-06T12:00:00Z',
    };

    const result = parseSseFrame({
      eventName: 'artifact-event',
      dataLines: [JSON.stringify(payload)],
    });

    expect(result).toEqual({
      kind: 'artifact-event',
      event: expect.objectContaining({ id: 'event-1', kind: 'WorkflowPackageSnapshot' }),
      supersedesPriorByName: false,
    });
  });
});
