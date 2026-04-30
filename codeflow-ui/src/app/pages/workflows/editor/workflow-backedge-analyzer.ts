export interface WorkflowBackedgeConnection {
  id: string;
  source: string;
  target: string;
  intentionalBackedge?: boolean;
  isSelected?: boolean;
}

export interface WorkflowBackedgeAnalysis {
  backedgeIds: Set<string>;
  cycleByConnectionId: Map<string, string[]>;
}

export interface WorkflowConnectionStyleTarget {
  element: HTMLElement;
  connection: WorkflowBackedgeConnection;
}

export class WorkflowBackedgeAnalyzer {
  static recompute(
    nodeIds: readonly string[],
    connections: readonly WorkflowBackedgeConnection[],
    nodeLabelFor: (nodeId: string) => string
  ): WorkflowBackedgeAnalysis {
    const backedgeIds = new Set<string>();
    const cycleByConnectionId = new Map<string, string[]>();

    if (connections.length === 0 || nodeIds.length === 0) {
      return { backedgeIds, cycleByConnectionId };
    }

    type Edge = { id: string; source: string; target: string };
    const outgoing = new Map<string, Edge[]>();
    const incomingCount = new Map<string, number>();
    for (const id of nodeIds) {
      outgoing.set(id, []);
      incomingCount.set(id, 0);
    }

    for (const connection of connections) {
      const list = outgoing.get(connection.source);
      if (!list) continue;
      list.push({ id: connection.id, source: connection.source, target: connection.target });
      incomingCount.set(connection.target, (incomingCount.get(connection.target) ?? 0) + 1);
    }

    type Color = 'white' | 'gray' | 'black';
    const color = new Map<string, Color>();
    for (const id of nodeIds) color.set(id, 'white');

    const rootOrder = [
      ...nodeIds.filter(id => (incomingCount.get(id) ?? 0) === 0),
      ...nodeIds
    ];
    const seenRoot = new Set<string>();

    for (const root of rootOrder) {
      if (seenRoot.has(root)) continue;
      seenRoot.add(root);
      if (color.get(root) !== 'white') continue;

      const stack: { nodeId: string; nextEdge: number }[] = [];
      color.set(root, 'gray');
      stack.push({ nodeId: root, nextEdge: 0 });

      while (stack.length > 0) {
        const frame = stack[stack.length - 1];
        const edges = outgoing.get(frame.nodeId);
        if (!edges || frame.nextEdge >= edges.length) {
          color.set(frame.nodeId, 'black');
          stack.pop();
          continue;
        }

        const edge = edges[frame.nextEdge];
        frame.nextEdge += 1;

        const targetColor = color.get(edge.target);
        if (targetColor === 'white') {
          color.set(edge.target, 'gray');
          stack.push({ nodeId: edge.target, nextEdge: 0 });
        } else if (targetColor === 'gray') {
          const cycle: string[] = [];
          let collecting = false;
          for (const f of stack) {
            if (!collecting && f.nodeId === edge.target) collecting = true;
            if (collecting) cycle.push(nodeLabelFor(f.nodeId));
          }
          if (cycle.length === 0) cycle.push(nodeLabelFor(edge.target));
          backedgeIds.add(edge.id);
          cycleByConnectionId.set(edge.id, cycle);
        }
      }
    }

    return { backedgeIds, cycleByConnectionId };
  }

  static applyConnectionStyles(
    target: WorkflowConnectionStyleTarget,
    analysis: WorkflowBackedgeAnalysis
  ): void {
    const path = target.element.querySelector('path') as SVGPathElement | null;
    if (!path) return;

    const connection = target.connection;
    const isLiveBackedge = analysis.backedgeIds.has(connection.id) && !connection.intentionalBackedge;
    const cycleMembers = analysis.cycleByConnectionId.get(connection.id);

    path.style.cursor = 'pointer';
    path.style.pointerEvents = 'auto';
    path.style.transition = 'stroke 120ms ease, stroke-width 120ms ease, filter 120ms ease';
    if (connection.isSelected) {
      path.style.stroke = '#ffd166';
      path.style.strokeWidth = '7px';
      path.style.filter = 'drop-shadow(0 0 6px rgba(255, 209, 102, 0.45))';
    } else if (isLiveBackedge) {
      path.style.stroke = '#f5b84c';
      path.style.strokeWidth = '5px';
      path.style.filter = '';
    } else {
      path.style.stroke = '#4682b4';
      path.style.strokeWidth = '5px';
      path.style.filter = '';
    }
    path.style.strokeDasharray = isLiveBackedge ? '10 6' : '';

    if (isLiveBackedge && cycleMembers && cycleMembers.length > 0) {
      const memberList = cycleMembers.join(' -> ');
      target.element.title =
        `This edge creates a cycle: ${memberList} -> ${cycleMembers[0]}. ` +
        'ReviewLoop iteration handles loops natively - verify the backedge is intentional.';
    } else if (target.element.title) {
      target.element.title = '';
    }
  }
}
