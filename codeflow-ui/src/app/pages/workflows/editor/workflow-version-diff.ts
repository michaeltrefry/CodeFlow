import { WorkflowDetail, WorkflowEdge, WorkflowNode } from '../../../core/models';

export interface NodeAddedDiff { kind: 'added'; node: WorkflowNode; }
export interface NodeRemovedDiff { kind: 'removed'; node: WorkflowNode; }
export interface NodeChangedDiff {
  kind: 'changed';
  before: WorkflowNode;
  after: WorkflowNode;
  changes: NodeFieldChange[];
}
export type NodeDiff = NodeAddedDiff | NodeRemovedDiff | NodeChangedDiff;

export interface NodeFieldChange {
  field: NodeChangeField;
  /** Display label (used by the diff UI). */
  label: string;
  /** True when the change is purely cosmetic (layout) — UI lists it last and de-emphasizes it. */
  cosmetic: boolean;
  before: unknown;
  after: unknown;
}

export type NodeChangeField =
  | 'kind'
  | 'agentPin'
  | 'subflowPin'
  | 'outputPorts'
  | 'inputScript'
  | 'outputScript'
  | 'reviewMaxRounds'
  | 'loopDecision'
  | 'layout';

export interface EdgeAddedDiff { kind: 'added'; edge: WorkflowEdge; }
export interface EdgeRemovedDiff { kind: 'removed'; edge: WorkflowEdge; }
export interface EdgeChangedDiff {
  kind: 'changed';
  before: WorkflowEdge;
  after: WorkflowEdge;
  /** Which fields differ. */
  changedFields: ('rotatesRound' | 'sortOrder' | 'intentionalBackedge')[];
}
export type EdgeDiff = EdgeAddedDiff | EdgeRemovedDiff | EdgeChangedDiff;

export interface MetadataChange {
  field: 'name' | 'category' | 'maxRoundsPerRound' | 'tags' | 'inputs';
  before: unknown;
  after: unknown;
}

export interface WorkflowVersionDiff {
  beforeKey: string;
  beforeVersion: number;
  afterVersion: number;
  /** True when no semantic differences were found (still useful for showing version metadata). */
  isEmpty: boolean;
  metadata: MetadataChange[];
  nodes: NodeDiff[];
  edges: EdgeDiff[];
}

const LAYOUT_TOLERANCE_PX = 0.5;

export function computeWorkflowVersionDiff(before: WorkflowDetail, after: WorkflowDetail): WorkflowVersionDiff {
  const metadata: MetadataChange[] = [];
  if (before.name !== after.name) metadata.push({ field: 'name', before: before.name, after: after.name });
  if (before.category !== after.category) metadata.push({ field: 'category', before: before.category, after: after.category });
  if (before.maxRoundsPerRound !== after.maxRoundsPerRound) {
    metadata.push({ field: 'maxRoundsPerRound', before: before.maxRoundsPerRound, after: after.maxRoundsPerRound });
  }
  if (!arraysEqualIgnoringOrder(before.tags ?? [], after.tags ?? [])) {
    metadata.push({ field: 'tags', before: before.tags ?? [], after: after.tags ?? [] });
  }
  if (!inputsEqual(before.inputs, after.inputs)) {
    metadata.push({ field: 'inputs', before: before.inputs, after: after.inputs });
  }

  const nodes = diffNodes(before.nodes, after.nodes);
  const edges = diffEdges(before.edges, after.edges);

  const isEmpty = metadata.length === 0 && nodes.length === 0 && edges.length === 0;

  return {
    beforeKey: before.key,
    beforeVersion: before.version,
    afterVersion: after.version,
    isEmpty,
    metadata,
    nodes,
    edges,
  };
}

function diffNodes(beforeList: WorkflowNode[], afterList: WorkflowNode[]): NodeDiff[] {
  const beforeById = new Map(beforeList.map(n => [n.id, n]));
  const afterById = new Map(afterList.map(n => [n.id, n]));
  const result: NodeDiff[] = [];

  // Removed first so the visual order is removed → changed → added (intuitive read).
  for (const before of beforeList) {
    if (!afterById.has(before.id)) {
      result.push({ kind: 'removed', node: before });
    }
  }

  for (const after of afterList) {
    const before = beforeById.get(after.id);
    if (!before) {
      result.push({ kind: 'added', node: after });
      continue;
    }
    const changes = diffNodeFields(before, after);
    if (changes.length > 0) {
      result.push({ kind: 'changed', before, after, changes });
    }
  }

  return result;
}

function diffNodeFields(a: WorkflowNode, b: WorkflowNode): NodeFieldChange[] {
  const changes: NodeFieldChange[] = [];

  if (a.kind !== b.kind) {
    changes.push({ field: 'kind', label: 'Node kind', cosmetic: false, before: a.kind, after: b.kind });
  }

  // Agent pin — combine key+version into one logical change.
  if ((a.agentKey ?? null) !== (b.agentKey ?? null) || (a.agentVersion ?? null) !== (b.agentVersion ?? null)) {
    changes.push({
      field: 'agentPin',
      label: 'Agent pin',
      cosmetic: false,
      before: formatPin(a.agentKey, a.agentVersion),
      after: formatPin(b.agentKey, b.agentVersion),
    });
  }

  if ((a.subflowKey ?? null) !== (b.subflowKey ?? null) || (a.subflowVersion ?? null) !== (b.subflowVersion ?? null)) {
    changes.push({
      field: 'subflowPin',
      label: 'Subflow pin',
      cosmetic: false,
      before: formatPin(a.subflowKey, a.subflowVersion),
      after: formatPin(b.subflowKey, b.subflowVersion),
    });
  }

  if (!arraysEqualOrdered(a.outputPorts ?? [], b.outputPorts ?? [])) {
    changes.push({
      field: 'outputPorts',
      label: 'Output ports',
      cosmetic: false,
      before: a.outputPorts ?? [],
      after: b.outputPorts ?? [],
    });
  }

  if ((a.inputScript ?? '') !== (b.inputScript ?? '')) {
    changes.push({
      field: 'inputScript',
      label: 'Input script',
      cosmetic: false,
      before: a.inputScript ?? '',
      after: b.inputScript ?? '',
    });
  }

  if ((a.outputScript ?? '') !== (b.outputScript ?? '')) {
    changes.push({
      field: 'outputScript',
      label: 'Output script',
      cosmetic: false,
      before: a.outputScript ?? '',
      after: b.outputScript ?? '',
    });
  }

  if ((a.reviewMaxRounds ?? null) !== (b.reviewMaxRounds ?? null)) {
    changes.push({
      field: 'reviewMaxRounds',
      label: 'Review max rounds',
      cosmetic: false,
      before: a.reviewMaxRounds,
      after: b.reviewMaxRounds,
    });
  }

  if ((a.loopDecision ?? null) !== (b.loopDecision ?? null)) {
    changes.push({
      field: 'loopDecision',
      label: 'Loop decision',
      cosmetic: false,
      before: a.loopDecision,
      after: b.loopDecision,
    });
  }

  // Layout-only repositioning — surface but tag as cosmetic so reviewers can de-emphasize.
  if (Math.abs((a.layoutX ?? 0) - (b.layoutX ?? 0)) > LAYOUT_TOLERANCE_PX
      || Math.abs((a.layoutY ?? 0) - (b.layoutY ?? 0)) > LAYOUT_TOLERANCE_PX) {
    changes.push({
      field: 'layout',
      label: 'Position',
      cosmetic: true,
      before: { x: a.layoutX, y: a.layoutY },
      after: { x: b.layoutX, y: b.layoutY },
    });
  }

  return changes;
}

function diffEdges(beforeList: WorkflowEdge[], afterList: WorkflowEdge[]): EdgeDiff[] {
  const beforeByKey = new Map(beforeList.map(e => [edgeKey(e), e]));
  const afterByKey = new Map(afterList.map(e => [edgeKey(e), e]));
  const result: EdgeDiff[] = [];

  for (const e of beforeList) {
    if (!afterByKey.has(edgeKey(e))) result.push({ kind: 'removed', edge: e });
  }

  for (const after of afterList) {
    const before = beforeByKey.get(edgeKey(after));
    if (!before) {
      result.push({ kind: 'added', edge: after });
      continue;
    }
    const changedFields: ('rotatesRound' | 'sortOrder' | 'intentionalBackedge')[] = [];
    if (before.rotatesRound !== after.rotatesRound) changedFields.push('rotatesRound');
    if (before.sortOrder !== after.sortOrder) changedFields.push('sortOrder');
    if ((before.intentionalBackedge ?? false) !== (after.intentionalBackedge ?? false)) {
      changedFields.push('intentionalBackedge');
    }
    if (changedFields.length > 0) {
      result.push({ kind: 'changed', before, after, changedFields });
    }
  }

  return result;
}

function edgeKey(e: WorkflowEdge): string {
  return `${e.fromNodeId}|${e.fromPort}->${e.toNodeId}|${e.toPort}`;
}

function formatPin(key?: string | null, version?: number | null): string | null {
  if (!key) return null;
  if (version === null || version === undefined) return key;
  return `${key}@v${version}`;
}

function arraysEqualOrdered(a: readonly string[], b: readonly string[]): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
  return true;
}

function arraysEqualIgnoringOrder(a: readonly string[], b: readonly string[]): boolean {
  if (a.length !== b.length) return false;
  const aSorted = [...a].sort();
  const bSorted = [...b].sort();
  for (let i = 0; i < aSorted.length; i++) if (aSorted[i] !== bSorted[i]) return false;
  return true;
}

function inputsEqual(a: readonly { key: string; ordinal: number; required: boolean; kind: string; defaultValueJson?: string | null }[],
                    b: readonly { key: string; ordinal: number; required: boolean; kind: string; defaultValueJson?: string | null }[]): boolean {
  if (a.length !== b.length) return false;
  const aSorted = [...a].sort((x, y) => x.ordinal - y.ordinal);
  const bSorted = [...b].sort((x, y) => x.ordinal - y.ordinal);
  for (let i = 0; i < aSorted.length; i++) {
    const x = aSorted[i], y = bSorted[i];
    if (x.key !== y.key || x.required !== y.required || x.kind !== y.kind
        || (x.defaultValueJson ?? '') !== (y.defaultValueJson ?? '')) {
      return false;
    }
  }
  return true;
}

/**
 * One-line summary chip text (used in version list + acceptance criterion: "Bumping with single
 * agent-pin change produces a one-line diff in the version history").
 */
export function summarizeDiff(diff: WorkflowVersionDiff): string {
  if (diff.isEmpty) return 'No semantic changes';
  const parts: string[] = [];
  const added = diff.nodes.filter(n => n.kind === 'added').length;
  const removed = diff.nodes.filter(n => n.kind === 'removed').length;
  const changed = diff.nodes.filter(n => n.kind === 'changed').length;
  if (added) parts.push(`+${added} node${added === 1 ? '' : 's'}`);
  if (removed) parts.push(`-${removed} node${removed === 1 ? '' : 's'}`);
  if (changed) parts.push(`~${changed} changed`);

  const edgesAdded = diff.edges.filter(e => e.kind === 'added').length;
  const edgesRemoved = diff.edges.filter(e => e.kind === 'removed').length;
  if (edgesAdded) parts.push(`+${edgesAdded} edge${edgesAdded === 1 ? '' : 's'}`);
  if (edgesRemoved) parts.push(`-${edgesRemoved} edge${edgesRemoved === 1 ? '' : 's'}`);

  if (diff.metadata.length > 0) parts.push(`${diff.metadata.length} metadata`);

  // Special-case the canonical "single agent-pin bump" → one-line summary.
  if (added === 0 && removed === 0 && diff.edges.length === 0 && diff.metadata.length === 0
      && changed === 1) {
    const onlyChanged = diff.nodes.find(n => n.kind === 'changed') as NodeChangedDiff;
    const nonCosmetic = onlyChanged.changes.filter(c => !c.cosmetic);
    if (nonCosmetic.length === 1 && nonCosmetic[0].field === 'agentPin') {
      const before = nonCosmetic[0].before ?? '(none)';
      const after = nonCosmetic[0].after ?? '(none)';
      return `Agent pin: ${before} → ${after}`;
    }
  }

  return parts.length > 0 ? parts.join(', ') : 'Cosmetic changes only';
}
