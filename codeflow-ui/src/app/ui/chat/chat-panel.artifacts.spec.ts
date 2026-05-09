import { applyArtifactEvent, ArtifactEventView, artifactSaveCardId, buildSaveConfirmationView, hydrateArtifactEventViews } from './chat-panel.component';
import { ArtifactEvent, parseSseFrame } from '../../core/assistant-stream';
import { HydratedArtifactEvent } from '../../core/assistant.api';

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

describe('buildSaveConfirmationView for artifact source (AA-6)', () => {
  it('builds an apply chip with packageSource artifact + eventId from a preview_ok response', () => {
    const result = buildSaveConfirmationView(JSON.stringify({
      status: 'preview_ok',
      packageSource: 'artifact',
      conversationId: 'conv-1',
      eventId: 'evt-1',
      artifactName: 'draft.cf-workflow-package.json',
      entryPoint: { key: 'demo-flow', version: 1 },
      canApply: true,
    }), null);

    expect(result).toBeDefined();
    expect(result!.kind).toBe('save_workflow_package');
    expect(result!.packageSource).toBe('artifact');
    expect(result!.mode).toBe('apply');
    expect(result!.artifactEventId).toBe('evt-1');
    expect(result!.artifactName).toBe('draft.cf-workflow-package.json');
    expect(result!.confirmLabel).toBe('Save');
  });

  it('builds a resolve chip from a preview_conflicts artifact response', () => {
    const result = buildSaveConfirmationView(JSON.stringify({
      status: 'preview_conflicts',
      packageSource: 'artifact',
      conversationId: 'conv-1',
      eventId: 'evt-2',
      artifactName: 'draft.cf-workflow-package.json',
      entryPoint: { key: 'demo-flow', version: 1 },
      conflictCount: 2,
      refusedCount: 0,
    }), null);

    expect(result).toBeDefined();
    expect(result!.mode).toBe('resolve');
    expect(result!.packageSource).toBe('artifact');
    expect(result!.artifactEventId).toBe('evt-2');
    expect(result!.confirmLabel).toBe('Resolve');
    expect(result!.conflictCount).toBe(2);
  });

  it('returns undefined when an artifact response is missing the eventId', () => {
    // Defense-in-depth: a server bug that omitted eventId would otherwise produce a chip
    // with no apply path. Refuse to render so the user doesn't get stuck on a confirmable
    // chip that always errors.
    const result = buildSaveConfirmationView(JSON.stringify({
      status: 'preview_ok',
      packageSource: 'artifact',
      conversationId: 'conv-1',
      entryPoint: { key: 'demo', version: 1 },
    }), null);

    expect(result).toBeUndefined();
  });

  it('preserves the legacy inline-source path (no eventId required)', () => {
    // Regression guard: the artifact-source widening must not break the inline path.
    const result = buildSaveConfirmationView(JSON.stringify({
      status: 'preview_ok',
      entryPoint: { key: 'inline-flow', version: 1 },
    }), { schemaVersion: 'codeflow.workflow-package.v1', entryPoint: { key: 'inline-flow', version: 1 } });

    expect(result).toBeDefined();
    expect(result!.packageSource).toBe('inline');
    expect(result!.artifactEventId).toBeUndefined();
  });

  it('artifactSaveCardId produces a stable, distinguishable id per event', () => {
    expect(artifactSaveCardId('evt-1')).toBe('artifact-save:evt-1');
    expect(artifactSaveCardId('evt-1')).toBe(artifactSaveCardId('evt-1'));
    expect(artifactSaveCardId('evt-1')).not.toBe(artifactSaveCardId('evt-2'));
  });
});

describe('buildSaveConfirmationView for save_agent_package (AP-8)', () => {
  // The agent-package chip shares the workflow chip's view-model shape and click flow;
  // the discriminator is the chip `kind` and the noun in prompt wording. Until the
  // agent-side bridge endpoints land (deferred from AP-2), every apply path routes through
  // the imports-page handoff — the confirm label reflects that.

  it('emits kind=save_agent_package when the tool name flags an agent package', () => {
    const result = buildSaveConfirmationView(
      JSON.stringify({
        status: 'preview_ok',
        entryPoint: { key: 'demo-writer', version: 1 },
      }),
      { schemaVersion: 'codeflow.agent-package.v1', entryPoint: { key: 'demo-writer', version: 1 } },
      'save_agent_package',
    );

    expect(result).toBeDefined();
    expect(result!.kind).toBe('save_agent_package');
    expect(result!.packageSource).toBe('inline');
  });

  it("agent apply confirm label is 'Open in imports' (no inline POST endpoint yet)", () => {
    const result = buildSaveConfirmationView(
      JSON.stringify({ status: 'preview_ok', entryPoint: { key: 'demo-writer', version: 1 } }),
      { schemaVersion: 'codeflow.agent-package.v1', entryPoint: { key: 'demo-writer', version: 1 } },
      'save_agent_package',
    );

    expect(result!.confirmLabel).toBe('Open in imports');
  });

  it("agent resolve chip prompt names 'Agent package' instead of workflow", () => {
    // Inline source: pkg is present but lacks an entryPoint, so the prompt falls back to
    // the noun. The pkg satisfies `buildSaveConfirmationView`'s "inline-needs-a-package"
    // guard without smuggling an entry-point key into the prompt.
    const result = buildSaveConfirmationView(
      JSON.stringify({
        status: 'preview_conflicts',
        conflictCount: 1,
        refusedCount: 0,
      }),
      { schemaVersion: 'codeflow.agent-package.v1' },
      'save_agent_package',
    );

    expect(result).toBeDefined();
    expect(result!.kind).toBe('save_agent_package');
    expect(result!.mode).toBe('resolve');
    expect(result!.prompt).toContain('Agent package');
    expect(result!.confirmLabel).toBe('Resolve');
  });

  it('agent artifact-source apply chip carries packageSource + eventId through to the chip', () => {
    const result = buildSaveConfirmationView(
      JSON.stringify({
        status: 'preview_ok',
        packageSource: 'artifact',
        conversationId: 'conv-1',
        eventId: 'evt-7',
        artifactName: 'draft.cf-agent-package.json',
        entryPoint: { key: 'demo-writer', version: 1 },
      }),
      null,
      'save_agent_package',
    );

    expect(result).toBeDefined();
    expect(result!.kind).toBe('save_agent_package');
    expect(result!.packageSource).toBe('artifact');
    expect(result!.artifactEventId).toBe('evt-7');
    expect(result!.artifactName).toBe('draft.cf-agent-package.json');
  });

  it("agent draft-source apply chip requires snapshotId to render (same guard as workflow)", () => {
    // Defense in depth: if save_agent_package's draft path returns preview_ok without a
    // snapshotId, the chip refuses to render — same invariant as workflows.
    const noSnapshot = buildSaveConfirmationView(
      JSON.stringify({
        status: 'preview_ok',
        packageSource: 'draft',
        entryPoint: { key: 'demo-writer', version: 1 },
        // snapshotId missing
      }),
      null,
      'save_agent_package',
    );
    expect(noSnapshot).toBeUndefined();

    const withSnapshot = buildSaveConfirmationView(
      JSON.stringify({
        status: 'preview_ok',
        packageSource: 'draft',
        snapshotId: '00000000-0000-0000-0000-000000000001',
        entryPoint: { key: 'demo-writer', version: 1 },
      }),
      null,
      'save_agent_package',
    );
    expect(withSnapshot).toBeDefined();
    expect(withSnapshot!.kind).toBe('save_agent_package');
    expect(withSnapshot!.snapshotId).toBe('00000000-0000-0000-0000-000000000001');
  });

  it('default tool name (omitted) still produces a workflow chip — backward compat', () => {
    const result = buildSaveConfirmationView(
      JSON.stringify({ status: 'preview_ok', entryPoint: { key: 'wf', version: 1 } }),
      { schemaVersion: 'codeflow.workflow-package.v1', entryPoint: { key: 'wf', version: 1 } },
    );

    expect(result!.kind).toBe('save_workflow_package');
  });
});

describe('hydrateArtifactEventViews (AA-3 reload hydration)', () => {
  function makeHydrated(overrides: Partial<HydratedArtifactEvent> = {}): HydratedArtifactEvent {
    return {
      id: overrides.id ?? '00000000-0000-0000-0000-000000000001',
      conversationId: 'conv-1',
      sequence: overrides.sequence ?? 1,
      kind: overrides.kind ?? 'WorkflowPackageDraft',
      name: overrides.name ?? 'draft.cf-workflow-package.json',
      snapshotId: overrides.snapshotId ?? null,
      summary: overrides.summary ?? null,
      createdAtUtc: overrides.createdAtUtc ?? '2026-05-06T12:00:00Z',
      superseded: overrides.superseded ?? false,
      expired: overrides.expired ?? false,
    };
  }

  it('returns an empty array when the server omits the field (back-compat)', () => {
    expect(hydrateArtifactEventViews(undefined)).toEqual([]);
  });

  it('returns an empty array for an explicitly empty list', () => {
    expect(hydrateArtifactEventViews([])).toEqual([]);
  });

  it('preserves server-computed superseded / expired flags', () => {
    // Server walks the supersede chain and computes the booleans so the panel can render
    // directly without re-walking. A reload after a Set→Patch sequence should land an
    // active draft + a superseded prior draft.
    const hydrated = hydrateArtifactEventViews([
      makeHydrated({ id: 'd1', sequence: 1, superseded: true }),
      makeHydrated({ id: 'd2', sequence: 2, superseded: false }),
    ]);

    expect(hydrated).toHaveLength(2);
    const byId = new Map(hydrated.map(e => [e.id, e]));
    expect(byId.get('d1')!.superseded).toBe(true);
    expect(byId.get('d2')!.superseded).toBe(false);
  });

  it('renames `kind` to `artifactKind` to free the namespace from the discriminator', () => {
    // The view-model uses `artifactKind` so `kind` stays free for the ThreadEntry
    // discriminator. The hydration helper must do that rename or the thread builder breaks.
    const [view] = hydrateArtifactEventViews([
      makeHydrated({ kind: 'WorkflowPackageSnapshot' }),
    ]);

    expect(view.artifactKind).toBe('WorkflowPackageSnapshot');
    expect((view as unknown as { kind?: string }).kind).toBeUndefined();
  });

  it('falls back to false on missing superseded / expired flags', () => {
    // Belt-and-suspenders against a partial server payload (a future server bug or a stale
    // cached response). Treat undefined as "active" rather than throwing.
    const partial = { ...makeHydrated() } as Partial<HydratedArtifactEvent>;
    delete partial.superseded;
    delete partial.expired;
    const [view] = hydrateArtifactEventViews([partial as HydratedArtifactEvent]);

    expect(view.superseded).toBe(false);
    expect(view.expired).toBe(false);
  });
});
