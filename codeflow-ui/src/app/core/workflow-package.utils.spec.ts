import { summarizeWorkflowPackage } from './workflow-package.utils';

describe('summarizeWorkflowPackage', () => {
  it('summarizes the entry workflow and de-duplicates package references', () => {
    const summary = summarizeWorkflowPackage({
      schemaVersion: 'codeflow.workflow-package.v1',
      entryPoint: { key: 'triage-flow', version: 4 },
      workflows: [
        {
          key: 'triage-flow',
          name: 'Triage Flow',
          nodes: [{ id: 'start' }, { id: 'review' }],
          edges: [{ from: 'start', to: 'review' }],
        },
        { key: 'shared-escalation' },
        { key: 'shared-escalation' },
      ],
      agents: [
        { key: 'triage-agent' },
        { key: 'review-agent' },
        { key: 'triage-agent' },
        { name: 'missing-key' },
      ],
    });

    expect(summary).toEqual({
      workflowName: 'Triage Flow',
      entryPointKey: 'triage-flow',
      entryPointVersion: 4,
      nodeCount: 2,
      edgeCount: 1,
      agentKeys: ['triage-agent', 'review-agent'],
      subflowKeys: ['shared-escalation'],
      schemaVersion: 'codeflow.workflow-package.v1',
    });
  });

  it('rejects unknown package shapes without throwing', () => {
    expect(summarizeWorkflowPackage(null)).toBeNull();
    expect(summarizeWorkflowPackage({ schemaVersion: 'not-codeflow' })).toBeNull();
  });
});
