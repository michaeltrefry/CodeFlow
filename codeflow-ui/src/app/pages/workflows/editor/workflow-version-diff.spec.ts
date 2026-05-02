import type { WorkflowDetail, WorkflowEdge, WorkflowInput, WorkflowNode } from '../../../core/models';
import { computeWorkflowVersionDiff, summarizeDiff } from './workflow-version-diff';

describe('computeWorkflowVersionDiff', () => {
  it('treats tag order and input array order as non-semantic', () => {
    const before = workflow({
      tags: ['ops', 'urgent'],
      inputs: [
        input({ key: 'request', ordinal: 1 }),
        input({ key: 'customer', ordinal: 0 }),
      ],
    });
    const after = workflow({
      version: 2,
      tags: ['urgent', 'ops'],
      inputs: [
        input({ key: 'customer', ordinal: 0 }),
        input({ key: 'request', ordinal: 1 }),
      ],
    });

    const diff = computeWorkflowVersionDiff(before, after);

    expect(diff.isEmpty).toBe(true);
    expect(diff.metadata).toEqual([]);
  });

  it('summarizes a single agent pin bump as a direct before-to-after change', () => {
    const before = workflow({
      nodes: [node({ id: 'review', kind: 'Agent', agentKey: 'reviewer', agentVersion: 1 })],
    });
    const after = workflow({
      version: 2,
      nodes: [node({ id: 'review', kind: 'Agent', agentKey: 'reviewer', agentVersion: 2 })],
    });

    const diff = computeWorkflowVersionDiff(before, after);

    expect(diff.nodes).toHaveLength(1);
    expect(diff.nodes[0]).toMatchObject({
      kind: 'changed',
      changes: [
        {
          field: 'agentPin',
          cosmetic: false,
          before: 'reviewer@v1',
          after: 'reviewer@v2',
        },
      ],
    });
    expect(summarizeDiff(diff)).toBe('Agent pin: reviewer@v1 \u2192 reviewer@v2');
  });

  it('separates cosmetic layout movement from semantic node and edge changes', () => {
    const beforeEdge = edge({ intentionalBackedge: false });
    const afterEdge = edge({ intentionalBackedge: true });
    const before = workflow({
      nodes: [
        node({ id: 'logic', kind: 'Logic', outputPorts: ['A', 'B'], layoutX: 10, layoutY: 20 }),
      ],
      edges: [beforeEdge],
    });
    const after = workflow({
      version: 2,
      nodes: [
        node({
          id: 'logic',
          kind: 'Logic',
          outputPorts: ['B', 'A'],
          layoutX: 11,
          layoutY: 20,
        }),
      ],
      edges: [afterEdge],
    });

    const diff = computeWorkflowVersionDiff(before, after);
    const changedNode = diff.nodes[0];
    const changedEdge = diff.edges[0];

    expect(changedNode).toMatchObject({ kind: 'changed' });
    if (changedNode.kind !== 'changed') throw new Error('Expected changed node diff');
    expect(changedNode.changes.map(change => [change.field, change.cosmetic])).toEqual([
      ['outputPorts', false],
      ['layout', true],
    ]);

    expect(changedEdge).toMatchObject({
      kind: 'changed',
      changedFields: ['intentionalBackedge'],
    });
  });
});

function workflow(overrides: Partial<WorkflowDetail> = {}): WorkflowDetail {
  return {
    key: 'triage-flow',
    version: 1,
    name: 'Triage Flow',
    maxRoundsPerRound: 3,
    category: 'Workflow',
    tags: [],
    createdAtUtc: '2026-04-29T00:00:00Z',
    isRetired: false,
    nodes: [],
    edges: [],
    inputs: [],
    ...overrides,
  };
}

function node(overrides: Partial<WorkflowNode> = {}): WorkflowNode {
  return {
    id: 'start',
    kind: 'Start',
    outputPorts: ['Completed'],
    layoutX: 0,
    layoutY: 0,
    ...overrides,
  };
}

function edge(overrides: Partial<WorkflowEdge> = {}): WorkflowEdge {
  return {
    fromNodeId: 'start',
    fromPort: 'Completed',
    toNodeId: 'next',
    toPort: 'in',
    rotatesRound: false,
    sortOrder: 0,
    ...overrides,
  };
}

function input(overrides: Partial<WorkflowInput> = {}): WorkflowInput {
  return {
    key: 'request',
    displayName: 'Request',
    kind: 'Text',
    required: true,
    ordinal: 0,
    ...overrides,
  };
}
