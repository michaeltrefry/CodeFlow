import {
  WorkflowCategory,
  WorkflowDetail,
  WorkflowEdge,
  WorkflowInput,
  WorkflowNode,
  WorkflowNodeKind
} from '../../../core/models';
import { WorkflowPayload } from '../../../core/workflows.api';
import { NodeEditor } from 'rete';
import { AreaPlugin } from 'rete-area-plugin';
import { IMPLICIT_FAILED_PORT, WorkflowAreaExtra, WorkflowEditorConnection, WorkflowEditorNode, WorkflowSchemes } from './workflow-node-schemes';

/**
 * Default declared ports when a node is first added to the canvas.
 * Under the user-defined-ports model: Agent/Hitl/Start nodes inherit ports from the pinned
 * agent's `outputs` declaration, Subflow/ReviewLoop nodes inherit terminal ports from the
 * pinned child workflow, and Logic nodes are author-driven. The implicit `Failed` port is
 * always wirable and is never part of `outputPorts`.
 */
export const DEFAULT_REVIEW_LOOP_MAX_ROUNDS = 3;

export function defaultOutputPortsFor(kind: WorkflowNodeKind): string[] {
  switch (kind) {
    case 'Start':
    case 'Agent':
    case 'Hitl':
      // Filled in once the author picks an agent; the picker derives port names from the
      // agent's declared outputs.
      return [];
    case 'Logic':
      return ['A', 'B'];
    case 'Subflow':
    case 'ReviewLoop':
      // Filled in once the author picks a child workflow; ports inherit from the child's
      // terminal-port set.
      return [];
    case 'Transform':
      // Transform exposes a single synthesized "Out" port (validator allows [] or ['Out']).
      // Declare it explicitly so the canvas renders the wirable handle.
      return ['Out'];
  }
}

export interface WorkflowModel {
  key: string;
  name: string;
  maxRoundsPerRound: number;
  category: WorkflowCategory;
  tags: string[];
  nodes: WorkflowNode[];
  edges: WorkflowEdge[];
  inputs: WorkflowInput[];
}

export function workflowDetailToModel(detail: WorkflowDetail): WorkflowModel {
  return {
    key: detail.key,
    name: detail.name,
    maxRoundsPerRound: detail.maxRoundsPerRound,
    category: detail.category,
    tags: detail.tags ?? [],
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
    category: 'Workflow',
    tags: [],
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
      outputScript: node.outputScript,
      inputScript: node.inputScript,
      outputPorts: node.outputPorts,
      subflowKey: node.subflowKey,
      subflowVersion: node.subflowVersion,
      reviewMaxRounds: node.reviewMaxRounds,
      loopDecision: node.loopDecision,
      template: node.template,
      outputType: node.outputType
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
    connection.intentionalBackedge = edge.intentionalBackedge ?? false;
    await editor.addConnection(connection);
  }

  return idToNode;
}

export function labelFor(node: Pick<WorkflowNode, 'kind' | 'agentKey' | 'subflowKey' | 'subflowVersion' | 'reviewMaxRounds'>): string {
  switch (node.kind) {
    case 'Start': return `Start — ${node.agentKey ?? '(pick agent)'}`;
    case 'Agent': return node.agentKey ?? '(pick agent)';
    case 'Hitl': return `HITL — ${node.agentKey ?? '(pick agent)'}`;
    case 'Logic': return 'Logic';
    case 'Subflow': {
      const key = node.subflowKey ?? '(pick workflow)';
      const version = node.subflowVersion ? `v${node.subflowVersion}` : 'latest';
      return `Subflow — ${key} ${version}`;
    }
    case 'ReviewLoop': {
      const key = node.subflowKey ?? '(pick workflow)';
      const rounds = node.reviewMaxRounds ? `×${node.reviewMaxRounds}` : '×?';
      return `ReviewLoop ${rounds} — ${key}`;
    }
    case 'Transform': return 'Transform';
  }
}

export function serializeEditor(
  editor: NodeEditor<WorkflowSchemes>,
  area: AreaPlugin<WorkflowSchemes, WorkflowAreaExtra>,
  meta: { key: string; name: string; maxRoundsPerRound: number; category: WorkflowCategory; tags: string[]; inputs: WorkflowInput[] }
): WorkflowPayload {
  const nodes: WorkflowNode[] = editor.getNodes().map(node => {
    const position = area.nodeViews.get(node.id)?.position ?? { x: 0, y: 0 };
    // The implicit Failed port is excluded from the serialized declaration — the API rejects
    // declaring it. `outputPortNames` already filters it; this filter is defensive.
    const declaredPorts = node.outputPortNames.filter(p => p !== IMPLICIT_FAILED_PORT);
    return {
      id: node.nodeId,
      kind: node.kind,
      agentKey: node.agentKey,
      agentVersion: node.agentVersion,
      outputScript: node.outputScript,
      inputScript: node.inputScript,
      outputPorts: declaredPorts,
      layoutX: position.x,
      layoutY: position.y,
      subflowKey: node.subflowKey,
      subflowVersion: node.subflowVersion,
      reviewMaxRounds: node.reviewMaxRounds,
      loopDecision: node.loopDecision,
      template: node.kind === 'Transform' ? node.template : null,
      outputType: node.kind === 'Transform' ? node.outputType : undefined
    };
  });

  const nodeById = new Map(editor.getNodes().map(n => [n.id, n.nodeId]));
  const edges: WorkflowEdge[] = editor.getConnections().map((conn, index) => ({
    fromNodeId: nodeById.get(conn.source) ?? conn.source,
    fromPort: conn.sourceOutput,
    toNodeId: nodeById.get(conn.target) ?? conn.target,
    toPort: conn.targetInput || 'in',
    rotatesRound: conn.rotatesRound,
    sortOrder: conn.sortOrder === 0 ? index : conn.sortOrder,
    intentionalBackedge: conn.intentionalBackedge
  }));

  return {
    key: meta.key,
    name: meta.name,
    maxRoundsPerRound: meta.maxRoundsPerRound,
    category: meta.category,
    tags: meta.tags,
    nodes,
    edges,
    inputs: meta.inputs
  };
}
