import {
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
    });
  });

  it('rejects malformed or non-preview save results', () => {
    const pkg = { schemaVersion: 'codeflow.workflow-package.v1' };

    expect(buildSaveConfirmationView('not json', pkg)).toBeUndefined();
    expect(buildSaveConfirmationView(JSON.stringify({ status: 'error' }), pkg)).toBeUndefined();
    expect(buildSaveConfirmationView(JSON.stringify({ status: 'preview_ok' }), undefined)).toBeUndefined();
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
