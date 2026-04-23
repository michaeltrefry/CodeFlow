import {
  WorkflowDetail,
  WorkflowEdge,
  WorkflowInput,
  WorkflowNode,
  WorkflowNodeKind
} from '../../../core/models';
import { WorkflowPayload } from '../../../core/workflows.api';
import { NodeEditor } from 'rete';
import { AreaPlugin } from 'rete-area-plugin';
import { WorkflowAreaExtra, WorkflowEditorConnection, WorkflowEditorNode, WorkflowSchemes } from './workflow-node-schemes';

/**
 * Default output ports when the agent has not published a richer output schema.
 * Every agent emits at least `Completed` (success) or `Failed` (error); workflow
 * authors can add more ports on a specific node if their agent emits other
 * decision kinds.
 */
export const DEFAULT_AGENT_OUTPUT_PORTS = ['Completed', 'Failed'];
export const SUBFLOW_OUTPUT_PORTS = ['Completed', 'Failed', 'Escalated'];

export function defaultOutputPortsFor(kind: WorkflowNodeKind): string[] {
  switch (kind) {
    case 'Start':
    case 'Agent':
    case 'Hitl':
      return [...DEFAULT_AGENT_OUTPUT_PORTS];
    case 'Escalation':
      return [];
    case 'Logic':
      return ['A', 'B'];
    case 'Subflow':
      return [...SUBFLOW_OUTPUT_PORTS];
  }
}

export interface WorkflowModel {
  key: string;
  name: string;
  maxRoundsPerRound: number;
  nodes: WorkflowNode[];
  edges: WorkflowEdge[];
  inputs: WorkflowInput[];
}

export function workflowDetailToModel(detail: WorkflowDetail): WorkflowModel {
  return {
    key: detail.key,
    name: detail.name,
    maxRoundsPerRound: detail.maxRoundsPerRound,
    nodes: detail.nodes,
    edges: detail.edges,
    inputs: detail.inputs
  };
}

export function emptyModel(): WorkflowModel {
  return {
    key: '',
    name: '',
    maxRoundsPerRound: 3,
    nodes: [],
    edges: [],
    inputs: []
  };
}

export async function loadIntoEditor(
  model: WorkflowModel,
  editor: NodeEditor<WorkflowSchemes>,
  area: AreaPlugin<WorkflowSchemes, WorkflowAreaExtra>
): Promise<Map<string, WorkflowEditorNode>> {
  const idToNode = new Map<string, WorkflowEditorNode>();

  for (const node of model.nodes) {
    const editorNode = new WorkflowEditorNode({
      nodeId: node.id,
      kind: node.kind,
      label: labelFor(node),
      agentKey: node.agentKey,
      agentVersion: node.agentVersion,
      script: node.script,
      outputPorts: node.outputPorts,
      subflowKey: node.subflowKey,
      subflowVersion: node.subflowVersion
    });
    idToNode.set(node.id, editorNode);
    await editor.addNode(editorNode);
    await area.translate(editorNode.id, { x: node.layoutX, y: node.layoutY });
  }

  for (const edge of model.edges) {
    const source = idToNode.get(edge.fromNodeId);
    const target = idToNode.get(edge.toNodeId);
    if (!source || !target) continue;

    const connection = new WorkflowEditorConnection(source, edge.fromPort, target, edge.toPort || 'in');
    connection.rotatesRound = edge.rotatesRound;
    connection.sortOrder = edge.sortOrder;
    await editor.addConnection(connection);
  }

  return idToNode;
}

export function labelFor(node: Pick<WorkflowNode, 'kind' | 'agentKey' | 'subflowKey' | 'subflowVersion'>): string {
  switch (node.kind) {
    case 'Start': return `Start — ${node.agentKey ?? '(pick agent)'}`;
    case 'Agent': return node.agentKey ?? '(pick agent)';
    case 'Hitl': return `HITL — ${node.agentKey ?? '(pick agent)'}`;
    case 'Escalation': return `Escalation — ${node.agentKey ?? '(pick agent)'}`;
    case 'Logic': return 'Logic';
    case 'Subflow': {
      const key = node.subflowKey ?? '(pick workflow)';
      const version = node.subflowVersion ? `v${node.subflowVersion}` : 'latest';
      return `Subflow — ${key} ${version}`;
    }
  }
}

export function serializeEditor(
  editor: NodeEditor<WorkflowSchemes>,
  area: AreaPlugin<WorkflowSchemes, WorkflowAreaExtra>,
  meta: { key: string; name: string; maxRoundsPerRound: number; inputs: WorkflowInput[] }
): WorkflowPayload {
  const nodes: WorkflowNode[] = editor.getNodes().map(node => {
    const position = area.nodeViews.get(node.id)?.position ?? { x: 0, y: 0 };
    return {
      id: node.nodeId,
      kind: node.kind,
      agentKey: node.agentKey,
      agentVersion: node.agentVersion,
      script: node.script,
      outputPorts: node.outputPortNames,
      layoutX: position.x,
      layoutY: position.y,
      subflowKey: node.subflowKey,
      subflowVersion: node.subflowVersion
    };
  });

  const nodeById = new Map(editor.getNodes().map(n => [n.id, n.nodeId]));
  const edges: WorkflowEdge[] = editor.getConnections().map((conn, index) => ({
    fromNodeId: nodeById.get(conn.source) ?? conn.source,
    fromPort: conn.sourceOutput,
    toNodeId: nodeById.get(conn.target) ?? conn.target,
    toPort: conn.targetInput || 'in',
    rotatesRound: conn.rotatesRound,
    sortOrder: conn.sortOrder === 0 ? index : conn.sortOrder
  }));

  return {
    key: meta.key,
    name: meta.name,
    maxRoundsPerRound: meta.maxRoundsPerRound,
    nodes,
    edges,
    inputs: meta.inputs
  };
}
