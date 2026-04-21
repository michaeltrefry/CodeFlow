import { NodeEditor } from 'rete';
import { AreaPlugin } from 'rete-area-plugin';
import { WorkflowAreaExtra, WorkflowEditorNode, WorkflowSchemes } from './workflow-node-schemes';

const COLUMN_WIDTH = 260;
const ROW_HEIGHT = 160;
const ORIGIN_X = 40;
const ORIGIN_Y = 40;

export async function tidyLayout(
  editor: NodeEditor<WorkflowSchemes>,
  area: AreaPlugin<WorkflowSchemes, WorkflowAreaExtra>
): Promise<void> {
  const nodes = editor.getNodes();
  if (nodes.length === 0) return;

  const nodeById = new Map(nodes.map(n => [n.id, n]));
  const outgoing = new Map<string, string[]>();
  const incoming = new Map<string, string[]>();
  for (const n of nodes) {
    outgoing.set(n.id, []);
    incoming.set(n.id, []);
  }
  for (const conn of editor.getConnections()) {
    outgoing.get(conn.source)?.push(conn.target);
    incoming.get(conn.target)?.push(conn.source);
  }

  const ranks = new Map<string, number>();

  // Start from explicit Start node if present, otherwise any node with no incoming edges.
  const seeds: WorkflowEditorNode[] = nodes.filter(n => n.kind === 'Start');
  if (seeds.length === 0) {
    seeds.push(...nodes.filter(n => (incoming.get(n.id) ?? []).length === 0));
  }
  if (seeds.length === 0) {
    // Cyclic graph with no obvious root — fall back to first node.
    seeds.push(nodes[0]);
  }

  // BFS rank assignment.
  const queue: Array<[string, number]> = seeds.map(s => [s.id, 0]);
  while (queue.length > 0) {
    const [id, rank] = queue.shift()!;
    const current = ranks.get(id);
    if (current !== undefined && current >= rank) continue;
    ranks.set(id, rank);
    for (const next of outgoing.get(id) ?? []) {
      queue.push([next, rank + 1]);
    }
  }

  // Any node the BFS missed (orphan / unreachable) — park in its own rank past the max.
  const knownMax = Math.max(-1, ...Array.from(ranks.values()));
  let orphanRank = knownMax + 1;
  for (const n of nodes) {
    if (!ranks.has(n.id)) {
      ranks.set(n.id, orphanRank++);
    }
  }

  // Group by rank, sort within a rank by kind then label for stability.
  const byRank = new Map<number, WorkflowEditorNode[]>();
  for (const [id, rank] of ranks.entries()) {
    const node = nodeById.get(id);
    if (!node) continue;
    if (!byRank.has(rank)) byRank.set(rank, []);
    byRank.get(rank)!.push(node);
  }

  for (const rankNodes of byRank.values()) {
    rankNodes.sort((a, b) => {
      if (a.kind !== b.kind) return a.kind.localeCompare(b.kind);
      return (a.label ?? '').localeCompare(b.label ?? '');
    });
  }

  // Assign positions.
  const sortedRanks = Array.from(byRank.keys()).sort((a, b) => a - b);
  for (const rank of sortedRanks) {
    const rankNodes = byRank.get(rank) ?? [];
    for (let i = 0; i < rankNodes.length; i++) {
      const node = rankNodes[i];
      const x = ORIGIN_X + rank * COLUMN_WIDTH;
      const y = ORIGIN_Y + i * ROW_HEIGHT;
      await area.translate(node.id, { x, y });
    }
  }
}
