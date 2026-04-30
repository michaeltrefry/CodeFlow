import { Injectable, inject, signal } from '@angular/core';
import { AgentsApi } from '../../../core/agents.api';
import { WorkflowsApi } from '../../../core/workflows.api';
import type { AgentConfig } from '../../../core/models';
import type { InPlaceEditTarget } from './agent-in-place-edit-dialog.component';
import type { PublishForkTarget } from './publish-fork-dialog.component';
import type { VersionUpdateTarget } from './version-update-dialog.component';
import type { WorkflowEditorNode } from './workflow-node-schemes';

export interface WorkflowCanvasDialogMessages {
  setStatus(message: string): void;
  setError(message: string): void;
}

export type VersionUpdateOutgoing = VersionUpdateTarget['outgoing'];

function isAgentBearingKind(kind: WorkflowEditorNode['kind']): boolean {
  return kind === 'Agent' || kind === 'Hitl' || kind === 'Start';
}

function latestPortsForReviewLoop(node: WorkflowEditorNode, terminals: readonly string[]): string[] {
  let latestPorts = terminals.slice();
  if (node.kind === 'ReviewLoop') {
    const loopDecision = (node.loopDecision?.trim()) || 'Rejected';
    latestPorts = latestPorts.filter(port => port !== loopDecision);
    if (!latestPorts.includes('Exhausted')) latestPorts.push('Exhausted');
  }
  return latestPorts;
}

@Injectable()
export class WorkflowCanvasDialogOrchestrator {
  private readonly agentsApi = inject(AgentsApi);
  private readonly workflowsApi = inject(WorkflowsApi);

  readonly editTarget = signal<InPlaceEditTarget | null>(null);
  readonly warningSuppressed = signal(false);
  readonly publishTarget = signal<PublishForkTarget | null>(null);
  readonly versionUpdateTarget = signal<VersionUpdateTarget | null>(null);
  readonly historyOpen = signal(false);

  openHistory(): void {
    this.historyOpen.set(true);
  }

  closeHistory(): void {
    this.historyOpen.set(false);
  }

  suppressWarning(): void {
    this.warningSuppressed.set(true);
  }

  openVersionUpdate(
    node: WorkflowEditorNode,
    outgoing: VersionUpdateOutgoing,
    messages: WorkflowCanvasDialogMessages
  ): void {
    if (isAgentBearingKind(node.kind) && node.agentKey) {
      const agentKey = node.agentKey;
      const fromVersion = node.agentVersion ?? 0;
      this.agentsApi.getLatest(agentKey).subscribe({
        next: latest => {
          if (!latest.version || latest.version <= fromVersion) {
            messages.setStatus(`Agent '${agentKey}' v${fromVersion} is already the latest.`);
            return;
          }
          const latestPorts = (latest.config?.outputs ?? [])
            .map(output => output.kind)
            .filter((kind): kind is string => typeof kind === 'string' && kind.length > 0);
          this.versionUpdateTarget.set({
            nodeId: node.id,
            kind: 'agent',
            refKey: agentKey,
            fromVersion,
            toVersion: latest.version,
            currentPorts: node.outputPortNames.slice(),
            latestPorts,
            outgoing,
          });
        },
        error: err => messages.setError(`Failed to load latest agent version: ${err?.message ?? err}`),
      });
      return;
    }

    if ((node.kind === 'Subflow' || node.kind === 'ReviewLoop') && node.subflowKey) {
      const subflowKey = node.subflowKey;
      const fromVersion = node.subflowVersion ?? 0;
      this.workflowsApi.getLatest(subflowKey).subscribe({
        next: latest => {
          if (!latest.version || latest.version <= fromVersion) {
            messages.setStatus(`Workflow '${subflowKey}' v${fromVersion} is already the latest.`);
            return;
          }
          this.workflowsApi.getTerminalPorts(subflowKey, latest.version).subscribe({
            next: terminals => {
              this.versionUpdateTarget.set({
                nodeId: node.id,
                kind: 'workflow',
                refKey: subflowKey,
                fromVersion,
                toVersion: latest.version,
                currentPorts: node.outputPortNames.slice(),
                latestPorts: latestPortsForReviewLoop(node, terminals),
                outgoing,
              });
            },
            error: err => messages.setError(`Failed to load latest workflow's terminal ports: ${err?.message ?? err}`),
          });
        },
        error: err => messages.setError(`Failed to load latest workflow version: ${err?.message ?? err}`),
      });
    }
  }

  cancelVersionUpdate(): void {
    this.versionUpdateTarget.set(null);
  }

  openEditInPlace(
    node: WorkflowEditorNode,
    workflowKey: string,
    setError: (message: string) => void
  ): void {
    if (!node.agentKey) return;
    if (!workflowKey) {
      setError('Pick a workflow key before editing an agent in place.');
      return;
    }

    const isExistingFork = node.agentKey.startsWith('__fork_');
    const load$ = node.agentVersion
      ? this.agentsApi.getVersion(node.agentKey, node.agentVersion)
      : this.agentsApi.getLatest(node.agentKey);

    load$.subscribe({
      next: version => {
        const config = (version.config ?? {}) as AgentConfig;
        const resolvedType = version.type === 'hitl' ? 'hitl' : 'agent';
        this.editTarget.set({
          nodeId: node.id,
          agentKey: version.key,
          agentVersion: version.version,
          workflowKey,
          initialConfig: config,
          initialType: resolvedType,
          isExistingFork
        });
      },
      error: err => setError(`Failed to load agent for in-place edit: ${err?.message ?? err}`)
    });
  }

  closeEditInPlace(): void {
    this.editTarget.set(null);
  }

  openPublishFork(node: WorkflowEditorNode): void {
    if (!node.agentKey || !node.agentKey.startsWith('__fork_')) return;
    this.publishTarget.set({ nodeId: node.id, forkKey: node.agentKey });
  }

  closePublishFork(): void {
    this.publishTarget.set(null);
  }
}
