import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { AgentsApi } from '../../../core/agents.api';
import { WorkflowsApi } from '../../../core/workflows.api';
import { WorkflowCanvasDialogOrchestrator } from './workflow-canvas-dialog-orchestrator.service';
import type { WorkflowEditorNode } from './workflow-node-schemes';

describe('WorkflowCanvasDialogOrchestrator', () => {
  let service: WorkflowCanvasDialogOrchestrator;
  let agentsApi: {
    getLatest: ReturnType<typeof vi.fn>;
    getVersion: ReturnType<typeof vi.fn>;
  };
  let workflowsApi: {
    getLatest: ReturnType<typeof vi.fn>;
    getTerminalPorts: ReturnType<typeof vi.fn>;
  };

  beforeEach(() => {
    agentsApi = {
      getLatest: vi.fn(),
      getVersion: vi.fn(),
    };
    workflowsApi = {
      getLatest: vi.fn(),
      getTerminalPorts: vi.fn(),
    };

    TestBed.configureTestingModule({
      providers: [
        WorkflowCanvasDialogOrchestrator,
        { provide: AgentsApi, useValue: agentsApi },
        { provide: WorkflowsApi, useValue: workflowsApi },
      ],
    });

    service = TestBed.inject(WorkflowCanvasDialogOrchestrator);
  });

  it('opens an agent version-update target from latest declared outputs', () => {
    agentsApi.getLatest.mockReturnValue(of({
      key: 'reviewer',
      version: 3,
      type: 'agent',
      config: { outputs: [{ kind: 'Approved' }, { kind: 'Rejected' }] },
    }));

    service.openVersionUpdate(
      node({ kind: 'Agent', agentKey: 'reviewer', agentVersion: 2, outputPortNames: ['Approved'] }),
      [{ sourcePort: 'Approved', targetLabel: 'finish' }],
      messages()
    );

    expect(service.versionUpdateTarget()).toEqual({
      nodeId: 'node-1',
      kind: 'agent',
      refKey: 'reviewer',
      fromVersion: 2,
      toVersion: 3,
      currentPorts: ['Approved'],
      latestPorts: ['Approved', 'Rejected'],
      outgoing: [{ sourcePort: 'Approved', targetLabel: 'finish' }],
    });
  });

  it('opens a ReviewLoop workflow version-update target with loop port filtered and Exhausted added', () => {
    workflowsApi.getLatest.mockReturnValue(of({ key: 'child-flow', version: 4 }));
    workflowsApi.getTerminalPorts.mockReturnValue(of(['Approved', 'Rejected']));

    service.openVersionUpdate(
      node({
        kind: 'ReviewLoop',
        subflowKey: 'child-flow',
        subflowVersion: 3,
        loopDecision: 'Rejected',
        outputPortNames: ['Approved'],
      }),
      [],
      messages()
    );

    expect(workflowsApi.getTerminalPorts).toHaveBeenCalledWith('child-flow', 4);
    expect(service.versionUpdateTarget()).toEqual(expect.objectContaining({
      kind: 'workflow',
      refKey: 'child-flow',
      latestPorts: ['Approved', 'Exhausted'],
    }));
  });

  it('opens an in-place edit target from a pinned agent version', () => {
    agentsApi.getVersion.mockReturnValue(of({
      key: 'writer',
      version: 5,
      type: 'hitl',
      config: { name: 'Writer' },
    }));

    service.openEditInPlace(
      node({ agentKey: 'writer', agentVersion: 5 }),
      'parent-workflow',
      message => { throw new Error(message); }
    );

    expect(agentsApi.getVersion).toHaveBeenCalledWith('writer', 5);
    expect(service.editTarget()).toEqual({
      nodeId: 'node-1',
      agentKey: 'writer',
      agentVersion: 5,
      workflowKey: 'parent-workflow',
      initialConfig: { name: 'Writer' },
      initialType: 'hitl',
      isExistingFork: false,
    });
  });

  it('owns simple dialog state transitions', () => {
    service.openHistory();
    service.suppressWarning();
    service.openPublishFork(node({ agentKey: '__fork_reviewer' }));

    expect(service.historyOpen()).toBe(true);
    expect(service.warningSuppressed()).toBe(true);
    expect(service.publishTarget()).toEqual({ nodeId: 'node-1', forkKey: '__fork_reviewer' });

    service.closeHistory();
    service.closePublishFork();

    expect(service.historyOpen()).toBe(false);
    expect(service.publishTarget()).toBeNull();
  });
});

function messages(): { setStatus: (message: string) => void; setError: (message: string) => void } {
  return {
    setStatus: message => { throw new Error(message); },
    setError: message => { throw new Error(message); },
  };
}

function node(overrides: Partial<WorkflowEditorNode>): WorkflowEditorNode {
  return {
    id: 'node-1',
    kind: 'Agent',
    agentKey: 'agent',
    agentVersion: 1,
    outputPortNames: [],
    subflowKey: null,
    subflowVersion: null,
    loopDecision: null,
    ...overrides,
  } as WorkflowEditorNode;
}
