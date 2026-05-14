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
import { FOR_EACH_CONTINUE_PORT, GOAL_ABANDONED_PORT, GOAL_BUDGET_LIMITED_PORT, GOAL_SUCCESS_PORT, IMPLICIT_FAILED_PORT, REVIEW_LOOP_EXHAUSTED_PORT, WorkflowAreaExtra, WorkflowEditorConnection, WorkflowEditorNode, WorkflowSchemes } from './workflow-node-schemes';

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
    case 'Swarm':
      // Swarm's terminal port is the synthesizer agent's declared output. Default to
      // "Synthesized" — matches the convention used by the hand-authored library entries
      // that pre-date the runtime; authors override via the agent picker.
      return ['Synthesized'];
    case 'ForEach':
      // ForEach has a single synthesized "Continue" port (sc-944). Authors never declare
      // ports; the canvas pads Continue here so loadIntoEditor's rete handle exists and
      // serializeEditor strips it before save the same way ReviewLoop strips Exhausted.
      return ['Continue'];
    case 'Goal':
      // Goal has three synthesized ports (epic 978): Success when the model calls
      // `goal.update(complete)` after the audit passes, BudgetLimited when the token budget is
      // exhausted before completion, and Abandoned when the model calls `goal.update(abandon)`
      // because the objective is environmentally impossible (GN-7, sc-990). Authors never
      // declare them; the canvas pads them in for rete and serializeEditor strips them on save
      // the same way as ForEach.
      return [GOAL_SUCCESS_PORT, GOAL_BUDGET_LIMITED_PORT, GOAL_ABANDONED_PORT];
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
    // ReviewLoop nodes always expose a synthesized `Exhausted` terminal port. Authoring flows
    // (workflow-canvas refreshSubflowPorts + canvas-dialog-orchestrator) add it client-side
    // before persisting, but workflows arriving from JSON imports / starter packages / older
    // hand-edited DB rows can omit it — and rete will throw "source node doesn't have output
    // with a key Exhausted" the moment we try to add the matching edge. Pad the declared
    // ports here so loadIntoEditor stays robust to those upstream variations.
    let declaredPorts = node.outputPorts;
    if (node.kind === 'ReviewLoop' && !declaredPorts.includes('Exhausted')) {
      declaredPorts = [...declaredPorts, 'Exhausted'];
    }
    // ForEach synthesizes its `Continue` terminal port server-side (sc-944 validator rejects
    // any author-declared port on ForEach), so workflows from the API arrive with an empty
    // outputPorts list. Pad it here so rete renders the wirable handle; serializeEditor
    // strips it back out before save the same way it strips ReviewLoop's Exhausted.
    if (node.kind === 'ForEach' && !declaredPorts.includes(FOR_EACH_CONTINUE_PORT)) {
      declaredPorts = [...declaredPorts, FOR_EACH_CONTINUE_PORT];
    }
    // Goal nodes synthesize Success + BudgetLimited + Abandoned server-side (epic 978 validator
    // rejects any author-declared port). Workflows from the API arrive with empty outputPorts;
    // pad all three here so rete renders wirable handles. serializeEditor strips them back out
    // on save.
    if (node.kind === 'Goal') {
      if (!declaredPorts.includes(GOAL_SUCCESS_PORT)) {
        declaredPorts = [...declaredPorts, GOAL_SUCCESS_PORT];
      }
      if (!declaredPorts.includes(GOAL_BUDGET_LIMITED_PORT)) {
        declaredPorts = [...declaredPorts, GOAL_BUDGET_LIMITED_PORT];
      }
      if (!declaredPorts.includes(GOAL_ABANDONED_PORT)) {
        declaredPorts = [...declaredPorts, GOAL_ABANDONED_PORT];
      }
    }
    const editorNode = new WorkflowEditorNode({
      nodeId: node.id,
      kind: node.kind,
      label: labelFor(node),
      agentKey: node.agentKey,
      agentVersion: node.agentVersion,
      outputScript: node.outputScript,
      inputScript: node.inputScript,
      outputPorts: declaredPorts,
      subflowKey: node.subflowKey,
      subflowVersion: node.subflowVersion,
      reviewMaxRounds: node.reviewMaxRounds,
      loopDecision: node.loopDecision,
      template: node.template,
      outputType: node.outputType,
      swarmProtocol: node.swarmProtocol,
      swarmN: node.swarmN,
      contributorAgentKey: node.contributorAgentKey,
      contributorAgentVersion: node.contributorAgentVersion,
      synthesizerAgentKey: node.synthesizerAgentKey,
      synthesizerAgentVersion: node.synthesizerAgentVersion,
      coordinatorAgentKey: node.coordinatorAgentKey,
      coordinatorAgentVersion: node.coordinatorAgentVersion,
      swarmTokenBudget: node.swarmTokenBudget,
      collectionExpression: node.collectionExpression,
      itemVar: node.itemVar,
      goalObjective: node.goalObjective,
      goalTokenBudget: node.goalTokenBudget,
      goalMaxIterations: node.goalMaxIterations,
      agentOverrides: node.agentOverrides
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

export function labelFor(
  node: Pick<WorkflowNode, 'kind' | 'agentKey' | 'subflowKey' | 'subflowVersion' | 'reviewMaxRounds'
    | 'outputType' | 'swarmProtocol' | 'swarmN' | 'collectionExpression' | 'itemVar'
    | 'goalObjective'>): string {
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
    case 'Transform': return `Transform → ${node.outputType ?? 'string'}`;
    case 'Swarm': {
      const protocol = node.swarmProtocol ?? '?';
      const n = node.swarmN ?? '?';
      return `Swarm ${protocol} ×${n}`;
    }
    case 'ForEach': {
      const expr = node.collectionExpression ?? '(pick collection)';
      const child = node.subflowKey ?? '(pick workflow)';
      return `ForEach ${expr} → ${child}`;
    }
    case 'Goal': {
      const objective = node.goalObjective?.trim();
      const preview = !objective
        ? '(set objective)'
        : objective.length > 24 ? objective.slice(0, 24) + '…' : objective;
      return `Goal — ${preview}`;
    }
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
    // declaring it. `outputPortNames` already filters it; this filter is defensive. ReviewLoop
    // additionally exposes a synthesized `Exhausted` handle the editor pads on load (so rete
    // can render edges from it); strip it on serialize too, otherwise the API rejects the save
    // for the same reason.
    const declaredPorts = node.outputPortNames
      .filter(p => p !== IMPLICIT_FAILED_PORT)
      .filter(p => !(node.kind === 'ReviewLoop' && p === REVIEW_LOOP_EXHAUSTED_PORT))
      // sc-944 validator rejects any author-declared port on a ForEach node — Continue is
      // synthesized server-side. Strip it on save the same way ReviewLoop strips Exhausted.
      .filter(p => !(node.kind === 'ForEach' && p === FOR_EACH_CONTINUE_PORT))
      // epic 978: Goal synthesizes Success + BudgetLimited + Abandoned server-side; validator
      // rejects any author-declared port on a Goal node. Strip all three on save.
      .filter(p => !(node.kind === 'Goal'
        && (p === GOAL_SUCCESS_PORT || p === GOAL_BUDGET_LIMITED_PORT || p === GOAL_ABANDONED_PORT)));
    const isSwarm = node.kind === 'Swarm';
    const isForEach = node.kind === 'ForEach';
    const isGoal = node.kind === 'Goal';
    // Validator rejects CoordinatorAgent* on Sequential, so suppress them unless the
    // configured protocol is Coordinator.
    const isCoordinator = isSwarm && node.swarmProtocol === 'Coordinator';
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
      outputType: node.kind === 'Transform' ? node.outputType : undefined,
      swarmProtocol: isSwarm ? node.swarmProtocol : null,
      swarmN: isSwarm ? node.swarmN : null,
      contributorAgentKey: isSwarm ? node.contributorAgentKey : null,
      contributorAgentVersion: isSwarm ? node.contributorAgentVersion : null,
      synthesizerAgentKey: isSwarm ? node.synthesizerAgentKey : null,
      synthesizerAgentVersion: isSwarm ? node.synthesizerAgentVersion : null,
      coordinatorAgentKey: isCoordinator ? node.coordinatorAgentKey : null,
      coordinatorAgentVersion: isCoordinator ? node.coordinatorAgentVersion : null,
      swarmTokenBudget: isSwarm ? node.swarmTokenBudget : null,
      collectionExpression: isForEach ? node.collectionExpression : null,
      itemVar: isForEach ? node.itemVar : null,
      goalObjective: isGoal ? node.goalObjective : null,
      goalTokenBudget: isGoal ? node.goalTokenBudget : null,
      goalMaxIterations: isGoal ? node.goalMaxIterations : null,
      // Epic 993: passed through untouched. The editor only surfaces the Overrides tab for
      // agent-bearing kinds (NO-8), and the API validator (NO-3) rejects overrides on any
      // other kind — so a non-agent node never carries a value here in practice.
      agentOverrides: node.agentOverrides ?? null
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
