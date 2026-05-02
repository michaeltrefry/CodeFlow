import {
  buildPackagePreviewFailureMessage,
  buildDraftSaveConfirmationView,
  buildPinnedMutationChip,
  buildReplayConfirmationView,
  buildRunConfirmationView,
  buildSaveConfirmationView,
  toCreateTraceRequest,
  toReplayRequestCached,
} from './chat-panel.component';

describe('chat panel confirmation helpers', () => {
  it('builds a save confirmation from a preview-ok result and cached package', () => {
    const confirmation = buildSaveConfirmationView(
      JSON.stringify({
        status: 'preview_ok',
        entryPoint: { key: 'triage-flow', version: 7 },
      }),
      {
        schemaVersion: 'codeflow.workflow-package.v1',
        entryPoint: { key: 'triage-flow', version: 7 },
        workflows: [
          {
            key: 'triage-flow',
            name: 'Triage Flow',
            nodes: [],
            edges: [],
          },
        ],
        agents: [],
      },
    );

    expect(confirmation).toEqual({
      kind: 'save_workflow_package',
      prompt: 'Save Triage Flow (triage-flow v7) to the library?',
      confirmLabel: 'Save',
      cancelLabel: 'Cancel',
      state: 'idle',
      packageSource: 'inline',
      snapshotId: undefined,
    });
  });

  it('rejects malformed or non-preview save results', () => {
    const pkg = { schemaVersion: 'codeflow.workflow-package.v1' };

    expect(buildSaveConfirmationView('not json', pkg)).toBeUndefined();
    expect(buildSaveConfirmationView(JSON.stringify({ status: 'error' }), pkg)).toBeUndefined();
    expect(buildSaveConfirmationView(JSON.stringify({ status: 'preview_ok' }), undefined)).toBeUndefined();
  });

  it('builds a save confirmation directly from a drafted workflow package', () => {
    const confirmation = buildDraftSaveConfirmationView({
      schemaVersion: 'codeflow.workflow-package.v1',
      entryPoint: { key: 'shortcut-pre-reqs', version: 1 },
      workflows: [
        {
          key: 'shortcut-pre-reqs',
          version: 1,
          name: 'Shortcut Pre-Reqs',
          nodes: [],
          edges: [],
        },
      ],
      agents: [],
    });

    expect(confirmation).toEqual({
      kind: 'save_workflow_package',
      prompt: 'Save Shortcut Pre-Reqs (shortcut-pre-reqs v1) to the library?',
      confirmLabel: 'Save',
      cancelLabel: 'Cancel',
      state: 'idle',
      packageSource: 'inline',
      snapshotId: undefined,
    });
  });

  it('keeps the pinned save chip visible after a successful save with a workflow link', () => {
    const chip = buildPinnedMutationChip({
      id: 'save-1',
      name: 'draft_workflow_package',
      status: 'success',
      confirmation: {
        kind: 'save_workflow_package',
        prompt: 'Save Shortcut Pre-Reqs?',
        confirmLabel: 'Save',
        cancelLabel: 'Cancel',
        state: 'success',
        applied: { kind: 'workflow', key: 'shortcut-pre-reqs', version: 3 },
      },
    });

    expect(chip).toEqual({
      id: 'save-1',
      state: 'success',
      title: 'Saved shortcut-pre-reqs v3 to the library.',
      message: 'The workflow is ready to load from the workflow library.',
      confirmLabel: 'Save',
      cancelLabel: 'Cancel',
      canConfirm: false,
      canCancel: false,
      linkUrl: '/workflows/shortcut-pre-reqs',
      linkLabel: 'Open workflow',
    });
  });

  it('keeps failed save chips retryable', () => {
    const chip = buildPinnedMutationChip({
      id: 'save-1',
      name: 'draft_workflow_package',
      status: 'success',
      confirmation: {
        kind: 'save_workflow_package',
        prompt: 'Save Shortcut Pre-Reqs?',
        state: 'error',
        errorMessage: 'Import failed.',
      },
    });

    expect(chip).toMatchObject({
      title: 'Workflow save failed.',
      message: 'Import failed.',
      confirmLabel: 'Retry',
      cancelLabel: 'Dismiss',
      canConfirm: true,
      canCancel: true,
    });
  });

  it('summarizes package preview conflicts before applying a save', () => {
    expect(buildPackagePreviewFailureMessage({
      entryPoint: { key: 'shortcut-pre-reqs', version: 1 },
      canApply: false,
      createCount: 0,
      reuseCount: 0,
      conflictCount: 2,
      warningCount: 0,
      warnings: [],
      items: [
        {
          kind: 'Workflow',
          key: 'shortcut-pre-reqs',
          version: 1,
          action: 'Conflict',
          message: 'A workflow with this key and version already exists with different content.',
        },
        {
          kind: 'Agent',
          key: 'planner',
          version: 1,
          action: 'Conflict',
          message: 'An agent with this key and version already exists with different config.',
        },
      ],
    })).toBe(
      'Workflow package cannot be saved: Workflow shortcut-pre-reqs v1 conflicts. '
      + 'A workflow with this key and version already exists with different content. (1 more conflict)',
    );
  });

  it('builds a run confirmation with workflow version and resolved input count', () => {
    const confirmation = buildRunConfirmationView(
      JSON.stringify({
        status: 'preview_ok',
        workflow: { key: 'triage-flow', name: 'Triage Flow', version: 4 },
        resolvedInputs: { request: 'hello', customer: 'acme' },
      }),
      { workflowKey: 'triage-flow', input: 'raw input' },
    );

    expect(confirmation).toEqual({
      kind: 'run_workflow',
      prompt: 'Run Triage Flow v4 with 2 inputs?',
      confirmLabel: 'Run',
      cancelLabel: 'Cancel',
      state: 'idle',
    });
  });

  it('parses run workflow tool arguments into a trace create request', () => {
    expect(toCreateTraceRequest({
      workflowKey: 'triage-flow',
      workflowVersion: 4,
      input: 'raw input',
      inputFileName: 'input.md',
      inputs: { customer: 'acme' },
    })).toEqual({
      workflowKey: 'triage-flow',
      workflowVersion: 4,
      input: 'raw input',
      inputFileName: 'input.md',
      inputs: { customer: 'acme' },
    });

    expect(toCreateTraceRequest({ workflowKey: 'triage-flow' })).toBeNull();
    expect(toCreateTraceRequest({ input: 'raw input' })).toBeNull();
  });

  it('builds a replay confirmation and cached replay request', () => {
    const cached = toReplayRequestCached({
      traceId: 'trace-123',
      force: true,
      workflowVersionOverride: 5,
      edits: [
        {
          agentKey: 'reviewer',
          ordinal: 2,
          decision: 'Approved',
          output: 'looks good',
          payload: { confidence: 0.9 },
        },
        {
          agentKey: '',
          ordinal: 3,
        },
      ],
    });

    expect(cached).toEqual({
      originalTraceId: 'trace-123',
      request: {
        force: true,
        workflowVersionOverride: 5,
        edits: [
          {
            agentKey: 'reviewer',
            ordinal: 2,
            decision: 'Approved',
            output: 'looks good',
            payload: { confidence: 0.9 },
          },
        ],
      },
    });

    const confirmation = buildReplayConfirmationView(
      JSON.stringify({
        status: 'preview_ok',
        workflowKey: 'triage-flow',
        edits: [{ agentKey: 'reviewer', ordinal: 2 }],
        force: true,
      }),
      cached ?? undefined,
    );

    expect(confirmation).toEqual({
      kind: 'propose_replay_with_edit',
      prompt: 'Replay triage-flow with 1 edit (force past drift)?',
      confirmLabel: 'Replay',
      cancelLabel: 'Cancel',
      state: 'idle',
    });
  });

  it('rejects replay requests without a trace id or valid edits', () => {
    expect(toReplayRequestCached({ edits: [{ agentKey: 'reviewer', ordinal: 1 }] })).toBeNull();
    expect(toReplayRequestCached({ traceId: 'trace-123', edits: [] })).toBeNull();
    expect(toReplayRequestCached({ traceId: 'trace-123', edits: [{ agentKey: '', ordinal: 0 }] })).toBeNull();
  });
});
