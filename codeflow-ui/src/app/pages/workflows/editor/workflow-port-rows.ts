import { AgentConfig } from '../../../core/models';

export type DerivedPortStatus = 'ok' | 'stale' | 'missing';

export interface DerivedPortRow {
  name: string;
  status: DerivedPortStatus;
}

export interface PortBearingNode {
  outputPortNames: readonly string[];
}

export function declaredOutputPorts(config: AgentConfig | null | undefined): string[] | null {
  if (!Array.isArray(config?.outputs)) return null;
  return config.outputs
    .map(o => o.kind)
    .filter((kind): kind is string => typeof kind === 'string' && kind.length > 0 && kind !== 'Failed');
}

/** Compares the node's author-facing ports with an agent's declared outputs. */
export function derivePortRows(
  node: PortBearingNode,
  config: AgentConfig | null | undefined
): DerivedPortRow[] {
  const declared = declaredOutputPorts(config);
  const declaredSet = declared ? new Set(declared) : null;
  const nodePorts = node.outputPortNames;
  const nodePortSet = new Set(nodePorts);

  const rows: DerivedPortRow[] = nodePorts.map(name => ({
    name,
    status: declaredSet
      ? (declaredSet.has(name) ? 'ok' : 'stale')
      : 'ok',
  }));

  if (declared) {
    for (const name of declared) {
      if (!nodePortSet.has(name)) {
        rows.push({ name, status: 'missing' });
      }
    }
  }

  return rows;
}

export function hasPortDrift(rows: readonly DerivedPortRow[]): boolean {
  return rows.some(row => row.status !== 'ok');
}
