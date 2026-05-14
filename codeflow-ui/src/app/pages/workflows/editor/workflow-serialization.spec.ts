import type { NodeEditor } from 'rete';
import type { AreaPlugin } from 'rete-area-plugin';
import type { WorkflowAreaExtra, WorkflowEditorConnection, WorkflowEditorNode, WorkflowSchemes } from './workflow-node-schemes';
import {
  FOR_EACH_CONTINUE_PORT,
  GOAL_ABANDONED_PORT,
  GOAL_BUDGET_LIMITED_PORT,
  GOAL_SUCCESS_PORT,
  IMPLICIT_FAILED_PORT,
  REVIEW_LOOP_EXHAUSTED_PORT,
} from './workflow-node-schemes';
import {
  defaultOutputPortsFor,
  labelFor,
  serializeEditor,
} from './workflow-serialization';

describe('defaultOutputPortsFor', () => {
  it('returns author-facing defaults by node kind', () => {
    expect(defaultOutputPortsFor('Start')).toEqual([]);
    expect(defaultOutputPortsFor('Agent')).toEqual([]);
    expect(defaultOutputPortsFor('Hitl')).toEqual([]);
    expect(defaultOutputPortsFor('Logic')).toEqual(['A', 'B']);
    expect(defaultOutputPortsFor('Subflow')).toEqual([]);
    expect(defaultOutputPortsFor('ReviewLoop')).toEqual([]);
    expect(defaultOutputPortsFor('Transform')).toEqual(['Out']);
    expect(defaultOutputPortsFor('Swarm')).toEqual(['Synthesized']);
    expect(defaultOutputPortsFor('ForEach')).toEqual(['Continue']);
    expect(defaultOutputPortsFor('Goal'))
      .toEqual([GOAL_SUCCESS_PORT, GOAL_BUDGET_LIMITED_PORT, GOAL_ABANDONED_PORT]);
  });
});

describe('labelFor', () => {
  it('formats labels around the pins and defaults authors see in the editor', () => {
    expect(labelFor({ kind: 'Start', agentKey: 'starter' })).toBe('Start \u2014 starter');
    expect(labelFor({ kind: 'Subflow', subflowKey: 'child', subflowVersion: 3 })).toBe('Subflow \u2014 child v3');
    expect(labelFor({ kind: 'ReviewLoop', subflowKey: 'review-child', reviewMaxRounds: 2 })).toBe('ReviewLoop \u00d72 \u2014 review-child');
    expect(labelFor({ kind: 'Transform', outputType: 'json' })).toBe('Transform \u2192 json');
    expect(labelFor({ kind: 'Swarm', swarmProtocol: 'Sequential', swarmN: 4 })).toBe('Swarm Sequential \u00d74');
    expect(labelFor({ kind: 'ForEach', collectionExpression: 'workflow.items', subflowKey: 'per-item' }))
      .toBe('ForEach workflow.items \u2192 per-item');
    expect(labelFor({ kind: 'Goal', goalObjective: 'Ship sc-979' })).toBe('Goal \u2014 Ship sc-979');
    expect(labelFor({ kind: 'Goal', goalObjective: null })).toBe('Goal \u2014 (set objective)');
    expect(labelFor({ kind: 'Goal', goalObjective: 'a'.repeat(40) }))
      .toBe('Goal \u2014 ' + 'a'.repeat(24) + '\u2026');
  });
});

describe('serializeEditor', () => {
  it('round-trips Sequential Swarm config and suppresses coordinator fields', () => {
    const sequentialSwarm = editorNode({
      id: 'rete-swarm-seq',
      nodeId: 'swarm-seq',
      kind: 'Swarm',
      outputPortNames: ['Synthesized', IMPLICIT_FAILED_PORT],
      swarmProtocol: 'Sequential',
      swarmN: 4,
      contributorAgentKey: 'contrib',
      contributorAgentVersion: 2,
      synthesizerAgentKey: 'synth',
      synthesizerAgentVersion: 1,
      // Set on the editor model — serializer must drop these on Sequential
      // because the validator rejects coordinator fields outside the Coordinator protocol.
      coordinatorAgentKey: 'leftover-coord',
      coordinatorAgentVersion: 9,
      swarmTokenBudget: 5000,
    });
    const editor = fakeEditor([sequentialSwarm], []);
    const area = fakeArea([['rete-swarm-seq', { x: 100, y: 100 }]]);

    const payload = serializeEditor(editor, area, {
      key: 'swarm-flow', name: 'Swarm Flow', maxRoundsPerRound: 1,
      category: 'Workflow', tags: [], inputs: [],
    });

    expect(payload.nodes[0]).toEqual(expect.objectContaining({
      id: 'swarm-seq',
      kind: 'Swarm',
      swarmProtocol: 'Sequential',
      swarmN: 4,
      contributorAgentKey: 'contrib',
      contributorAgentVersion: 2,
      synthesizerAgentKey: 'synth',
      synthesizerAgentVersion: 1,
      coordinatorAgentKey: null,
      coordinatorAgentVersion: null,
      swarmTokenBudget: 5000,
      outputPorts: ['Synthesized'],
    }));
  });

  it('round-trips Coordinator Swarm config including coordinator fields', () => {
    const coordinatorSwarm = editorNode({
      id: 'rete-swarm-coord',
      nodeId: 'swarm-coord',
      kind: 'Swarm',
      outputPortNames: ['Synthesized', IMPLICIT_FAILED_PORT],
      swarmProtocol: 'Coordinator',
      swarmN: 6,
      contributorAgentKey: 'contrib',
      contributorAgentVersion: 3,
      synthesizerAgentKey: 'synth',
      synthesizerAgentVersion: 2,
      coordinatorAgentKey: 'coord',
      coordinatorAgentVersion: 4,
      swarmTokenBudget: null,
    });
    const editor = fakeEditor([coordinatorSwarm], []);
    const area = fakeArea([['rete-swarm-coord', { x: 0, y: 0 }]]);

    const payload = serializeEditor(editor, area, {
      key: 'swarm-flow', name: 'Swarm Flow', maxRoundsPerRound: 1,
      category: 'Workflow', tags: [], inputs: [],
    });

    expect(payload.nodes[0]).toEqual(expect.objectContaining({
      kind: 'Swarm',
      swarmProtocol: 'Coordinator',
      swarmN: 6,
      coordinatorAgentKey: 'coord',
      coordinatorAgentVersion: 4,
      swarmTokenBudget: null,
    }));
  });

  it('does not emit swarm fields on non-Swarm node kinds', () => {
    const agentNode = editorNode({
      id: 'rete-agent',
      nodeId: 'agent-1',
      kind: 'Agent',
      outputPortNames: ['Approved', IMPLICIT_FAILED_PORT],
    });
    const editor = fakeEditor([agentNode], []);
    const area = fakeArea([['rete-agent', { x: 0, y: 0 }]]);

    const payload = serializeEditor(editor, area, {
      key: 'k', name: 'n', maxRoundsPerRound: 1,
      category: 'Workflow', tags: [], inputs: [],
    });

    expect(payload.nodes[0]).toEqual(expect.objectContaining({
      kind: 'Agent',
      swarmProtocol: null,
      swarmN: null,
      contributorAgentKey: null,
      synthesizerAgentKey: null,
      coordinatorAgentKey: null,
      swarmTokenBudget: null,
    }));
  });

  it('strips the synthesized Exhausted port from a ReviewLoop save payload', () => {
    // loadIntoEditor pads `Exhausted` onto a ReviewLoop's port list so rete can render the
    // edge handle, and refreshSubflowPorts adds it on subflow pick. The API rejects declaring
    // it for the same reason it rejects declaring `Failed` (both are runtime-synthesized).
    // Without this strip every ReviewLoop save round-trip fails the
    // `WorkflowValidator.CheckDeclaredPortReservations` rule with "declares the reserved
    // 'Exhausted' port in outputPorts" — repro for the user-reported edit-blocked issue.
    const reviewLoop = editorNode({
      id: 'rete-review',
      nodeId: 'review-1',
      kind: 'ReviewLoop',
      outputPortNames: ['Approved', REVIEW_LOOP_EXHAUSTED_PORT, IMPLICIT_FAILED_PORT],
      subflowKey: 'inner-flow',
      subflowVersion: 1,
      reviewMaxRounds: 3,
      loopDecision: 'Rejected',
    });
    const editor = fakeEditor([reviewLoop], []);
    const area = fakeArea([['rete-review', { x: 250, y: 200 }]]);

    const payload = serializeEditor(editor, area, {
      key: 'review-flow', name: 'Review Flow', maxRoundsPerRound: 3,
      category: 'Workflow', tags: [], inputs: [],
    });

    expect(payload.nodes[0]).toEqual(expect.objectContaining({
      id: 'review-1',
      kind: 'ReviewLoop',
      outputPorts: ['Approved'],
    }));
    expect(payload.nodes[0].outputPorts).not.toContain(REVIEW_LOOP_EXHAUSTED_PORT);
    expect(payload.nodes[0].outputPorts).not.toContain(IMPLICIT_FAILED_PORT);
  });

  it('round-trips ForEach config and strips the synthesized Continue port from the save payload', () => {
    // sc-944: ForEach synthesizes its `Continue` terminal port server-side and rejects any
    // author-declared port. The editor pads `Continue` onto the canvas so rete renders the
    // outgoing handle, but serializeEditor must strip it back out before save the same way
    // it strips ReviewLoop's `Exhausted` — otherwise the save trips the validator's
    // declared-port reservation rule.
    const forEach = editorNode({
      id: 'rete-foreach',
      nodeId: 'foreach-1',
      kind: 'ForEach',
      outputPortNames: [FOR_EACH_CONTINUE_PORT, IMPLICIT_FAILED_PORT],
      subflowKey: 'per-item-flow',
      subflowVersion: 1,
      collectionExpression: 'workflow.demoItems',
      itemVar: 'task',
    });
    const editor = fakeEditor([forEach], []);
    const area = fakeArea([['rete-foreach', { x: 300, y: 200 }]]);

    const payload = serializeEditor(editor, area, {
      key: 'foreach-flow', name: 'ForEach Flow', maxRoundsPerRound: 3,
      category: 'Workflow', tags: [], inputs: [],
    });

    expect(payload.nodes[0]).toEqual(expect.objectContaining({
      id: 'foreach-1',
      kind: 'ForEach',
      collectionExpression: 'workflow.demoItems',
      itemVar: 'task',
      subflowKey: 'per-item-flow',
      subflowVersion: 1,
      outputPorts: [],
    }));
    expect(payload.nodes[0].outputPorts).not.toContain(FOR_EACH_CONTINUE_PORT);
    expect(payload.nodes[0].outputPorts).not.toContain(IMPLICIT_FAILED_PORT);
  });

  it('does not emit ForEach fields on non-ForEach node kinds', () => {
    const agentNode = editorNode({
      id: 'rete-agent',
      nodeId: 'agent-1',
      kind: 'Agent',
      outputPortNames: ['Approved', IMPLICIT_FAILED_PORT],
      collectionExpression: 'leftover-expr',
      itemVar: 'leftover-var',
    });
    const editor = fakeEditor([agentNode], []);
    const area = fakeArea([['rete-agent', { x: 0, y: 0 }]]);

    const payload = serializeEditor(editor, area, {
      key: 'k', name: 'n', maxRoundsPerRound: 1,
      category: 'Workflow', tags: [], inputs: [],
    });

    expect(payload.nodes[0]).toEqual(expect.objectContaining({
      kind: 'Agent',
      collectionExpression: null,
      itemVar: null,
    }));
  });

  it('filters implicit failed ports and preserves canvas/connection metadata', () => {
    const agentNode = editorNode({
      id: 'rete-agent',
      nodeId: 'agent-1',
      kind: 'Agent',
      agentKey: 'triage',
      agentVersion: 7,
      outputPortNames: ['Approved', IMPLICIT_FAILED_PORT],
    });
    const transformNode = editorNode({
      id: 'rete-transform',
      nodeId: 'transform-1',
      kind: 'Transform',
      template: '{{ input | string.upcase }}',
      outputType: 'json',
      outputPortNames: ['Out', IMPLICIT_FAILED_PORT],
    });
    const editor = fakeEditor(
      [agentNode, transformNode],
      [
        connection({
          source: 'rete-agent',
          sourceOutput: 'Approved',
          target: 'rete-transform',
          targetInput: '',
          rotatesRound: true,
          sortOrder: 0,
          intentionalBackedge: true,
        }),
      ],
    );
    const area = fakeArea([
      ['rete-agent', { x: 120, y: 240 }],
      ['rete-transform', { x: 420, y: 240 }],
    ]);

    const payload = serializeEditor(editor, area, {
      key: 'triage-flow',
      name: 'Triage Flow',
      maxRoundsPerRound: 5,
      category: 'Workflow',
      tags: ['ops'],
      inputs: [],
    });

    expect(payload.nodes).toEqual([
      expect.objectContaining({
        id: 'agent-1',
        kind: 'Agent',
        agentKey: 'triage',
        agentVersion: 7,
        outputPorts: ['Approved'],
        layoutX: 120,
        layoutY: 240,
        template: null,
        outputType: undefined,
      }),
      expect.objectContaining({
        id: 'transform-1',
        kind: 'Transform',
        outputPorts: ['Out'],
        layoutX: 420,
        layoutY: 240,
        template: '{{ input | string.upcase }}',
        outputType: 'json',
      }),
    ]);
    expect(payload.edges).toEqual([
      {
        fromNodeId: 'agent-1',
        fromPort: 'Approved',
        toNodeId: 'transform-1',
        toPort: 'in',
        rotatesRound: true,
        sortOrder: 0,
        intentionalBackedge: true,
      },
    ]);
  });
});

function fakeEditor(
  nodes: WorkflowEditorNode[],
  connections: WorkflowEditorConnection[],
): NodeEditor<WorkflowSchemes> {
  return {
    getNodes: () => nodes,
    getConnections: () => connections,
  } as unknown as NodeEditor<WorkflowSchemes>;
}

function fakeArea(
  entries: Array<[string, { x: number; y: number }]>,
): AreaPlugin<WorkflowSchemes, WorkflowAreaExtra> {
  return {
    nodeViews: new Map(entries.map(([id, position]) => [id, { position }])),
  } as unknown as AreaPlugin<WorkflowSchemes, WorkflowAreaExtra>;
}

function editorNode(overrides: Partial<WorkflowEditorNode> & {
  id: string;
  nodeId: string;
  kind: WorkflowEditorNode['kind'];
  outputPortNames: string[];
}): WorkflowEditorNode {
  return {
    label: '',
    agentKey: null,
    agentVersion: null,
    outputScript: null,
    inputScript: null,
    subflowKey: null,
    subflowVersion: null,
    reviewMaxRounds: null,
    loopDecision: null,
    template: null,
    outputType: 'string',
    swarmProtocol: null,
    swarmN: null,
    contributorAgentKey: null,
    contributorAgentVersion: null,
    synthesizerAgentKey: null,
    synthesizerAgentVersion: null,
    coordinatorAgentKey: null,
    coordinatorAgentVersion: null,
    swarmTokenBudget: null,
    ...overrides,
  } as WorkflowEditorNode;
}

function connection(overrides: Partial<WorkflowEditorConnection>): WorkflowEditorConnection {
  return {
    source: 'source',
    sourceOutput: 'Completed',
    target: 'target',
    targetInput: 'in',
    rotatesRound: false,
    sortOrder: 0,
    intentionalBackedge: false,
    ...overrides,
  } as WorkflowEditorConnection;
}
