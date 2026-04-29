import type { NodeEditor } from 'rete';
import type { AreaPlugin } from 'rete-area-plugin';
import type { WorkflowAreaExtra, WorkflowEditorConnection, WorkflowEditorNode, WorkflowSchemes } from './workflow-node-schemes';
import {
  IMPLICIT_FAILED_PORT,
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
  });
});

describe('labelFor', () => {
  it('formats labels around the pins and defaults authors see in the editor', () => {
    expect(labelFor({ kind: 'Start', agentKey: 'starter' })).toBe('Start \u2014 starter');
    expect(labelFor({ kind: 'Subflow', subflowKey: 'child', subflowVersion: 3 })).toBe('Subflow \u2014 child v3');
    expect(labelFor({ kind: 'ReviewLoop', subflowKey: 'review-child', reviewMaxRounds: 2 })).toBe('ReviewLoop \u00d72 \u2014 review-child');
    expect(labelFor({ kind: 'Transform', outputType: 'json' })).toBe('Transform \u2192 json');
    expect(labelFor({ kind: 'Swarm', swarmProtocol: 'Sequential', swarmN: 4 })).toBe('Swarm Sequential \u00d74');
  });
});

describe('serializeEditor', () => {
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
