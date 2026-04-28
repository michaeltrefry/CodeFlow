/**
 * Lightweight client-side summary of a drafted workflow package (HAA-9). The assistant emits
 * a workflow package as a fenced code block with the `cf-workflow-package` language hint; the
 * chat renderer parses the JSON and shows a one-line summary above the collapsed JSON.
 *
 * This is intentionally tolerant: the assistant might emit a package with extra fields or
 * arrays we don't surface here. Parsing follows the wire DTO from {@link WorkflowPackageDocument}
 * but never throws — bad packages just produce empty / fallback summaries and the chat shows
 * the raw JSON without enhancement.
 */

export interface WorkflowPackageSummary {
  readonly workflowName: string;
  readonly entryPointKey: string;
  readonly entryPointVersion: number;
  readonly nodeCount: number;
  readonly edgeCount: number;
  readonly agentKeys: readonly string[];
  readonly subflowKeys: readonly string[];
  readonly schemaVersion: string;
}

/**
 * Returns a summary, or null if the JSON doesn't look like a workflow package.
 */
export function summarizeWorkflowPackage(json: unknown): WorkflowPackageSummary | null {
  if (!json || typeof json !== 'object') return null;
  const pkg = json as Record<string, unknown>;
  const schemaVersion = typeof pkg['schemaVersion'] === 'string' ? (pkg['schemaVersion'] as string) : '';
  if (!schemaVersion.startsWith('codeflow.workflow-package')) return null;

  const entryPoint = pkg['entryPoint'] as { key?: string; version?: number } | undefined;
  const workflows = Array.isArray(pkg['workflows']) ? (pkg['workflows'] as unknown[]) : [];
  const agents = Array.isArray(pkg['agents']) ? (pkg['agents'] as unknown[]) : [];

  const entryKey = entryPoint?.key ?? '';
  const entryVersion = typeof entryPoint?.version === 'number' ? entryPoint.version : 0;
  const entryWorkflow = workflows.find(
    (w): w is Record<string, unknown> =>
      !!w && typeof w === 'object' && (w as Record<string, unknown>)['key'] === entryKey,
  );

  const workflowName =
    entryWorkflow && typeof entryWorkflow['name'] === 'string'
      ? (entryWorkflow['name'] as string)
      : entryKey || 'Untitled workflow';

  const nodes = entryWorkflow && Array.isArray(entryWorkflow['nodes'])
    ? (entryWorkflow['nodes'] as unknown[])
    : [];
  const edges = entryWorkflow && Array.isArray(entryWorkflow['edges'])
    ? (entryWorkflow['edges'] as unknown[])
    : [];

  const agentKeys = agents
    .map(a => (a && typeof a === 'object' ? (a as Record<string, unknown>)['key'] : null))
    .filter((k): k is string => typeof k === 'string');

  const subflowKeys = workflows
    .filter(
      (w): w is Record<string, unknown> =>
        !!w && typeof w === 'object' && (w as Record<string, unknown>)['key'] !== entryKey,
    )
    .map(w => (typeof w['key'] === 'string' ? (w['key'] as string) : null))
    .filter((k): k is string => typeof k === 'string');

  return {
    workflowName,
    entryPointKey: entryKey,
    entryPointVersion: entryVersion,
    nodeCount: nodes.length,
    edgeCount: edges.length,
    agentKeys: Array.from(new Set(agentKeys)),
    subflowKeys: Array.from(new Set(subflowKeys)),
    schemaVersion,
  };
}
