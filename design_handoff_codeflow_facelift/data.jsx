// data.jsx — realistic mock data matching CodeFlow's domain models

const AGENTS = [
  { key: 'intake-router', latestVersion: 7, name: 'Intake router', provider: 'anthropic', model: 'claude-haiku-4-5', type: 'agent', latestCreatedAtUtc: '2026-04-22T14:11:00Z', latestCreatedBy: 'mtrefry' },
  { key: 'triage-classifier', latestVersion: 12, name: 'Support triage', provider: 'openai', model: 'gpt-5.4-mini', type: 'agent', latestCreatedAtUtc: '2026-04-22T09:45:00Z', latestCreatedBy: 'klee' },
  { key: 'technical-reviewer', latestVersion: 4, name: 'Technical reviewer', provider: 'anthropic', model: 'claude-sonnet-4-5', type: 'agent', latestCreatedAtUtc: '2026-04-21T19:02:00Z', latestCreatedBy: 'mtrefry' },
  { key: 'code-summarizer', latestVersion: 3, name: 'Code summarizer', provider: 'openai', model: 'gpt-5.4', type: 'agent', latestCreatedAtUtc: '2026-04-21T11:20:00Z', latestCreatedBy: 'jchen' },
  { key: 'diff-explainer', latestVersion: 2, name: 'Diff explainer', provider: 'lmstudio', model: 'qwen2.5-coder-32b', type: 'agent', latestCreatedAtUtc: '2026-04-20T20:14:00Z', latestCreatedBy: 'mtrefry' },
  { key: 'hitl-compliance', latestVersion: 1, name: 'Compliance reviewer', provider: null, model: null, type: 'hitl', latestCreatedAtUtc: '2026-04-19T08:33:00Z', latestCreatedBy: 'ops' },
  { key: 'pr-composer', latestVersion: 5, name: 'PR composer', provider: 'anthropic', model: 'claude-opus-4-5', type: 'agent', latestCreatedAtUtc: '2026-04-18T16:55:00Z', latestCreatedBy: 'jchen' },
  { key: 'spec-distiller', latestVersion: 2, name: 'Spec distiller', provider: 'openai', model: 'gpt-5.4', type: 'agent', latestCreatedAtUtc: '2026-04-18T10:08:00Z', latestCreatedBy: 'klee' },
  { key: 'hitl-design-review', latestVersion: 3, name: 'Design review', provider: null, model: null, type: 'hitl', latestCreatedAtUtc: '2026-04-17T15:12:00Z', latestCreatedBy: 'ops' }
];

const TRACES = [
  { traceId: '7a2f91e4-4a88-4c3e-92df-d4a6b19e2134', workflowKey: 'support-triage', workflowVersion: 14, currentState: 'Running',   currentAgentKey: 'triage-classifier',  roundCount: 3, createdAtUtc: '2026-04-23T14:22:00Z', updatedAtUtc: '2026-04-23T14:29:12Z' },
  { traceId: '5fe381bb-9dd4-4e10-b3aa-ff1c1d9fe9c2', workflowKey: 'pr-review',        workflowVersion: 8,  currentState: 'Escalated', currentAgentKey: 'hitl-compliance',     roundCount: 5, createdAtUtc: '2026-04-23T13:59:00Z', updatedAtUtc: '2026-04-23T14:27:40Z' },
  { traceId: '11f0a8cc-1b83-4d45-b991-2c5c6a23b410', workflowKey: 'support-triage',   workflowVersion: 14, currentState: 'Completed', currentAgentKey: 'pr-composer',         roundCount: 4, createdAtUtc: '2026-04-23T13:44:00Z', updatedAtUtc: '2026-04-23T13:51:18Z' },
  { traceId: '9c884a3b-2f41-4f0c-b321-8cd1de773a80', workflowKey: 'code-review',      workflowVersion: 6,  currentState: 'Failed',    currentAgentKey: 'technical-reviewer',  roundCount: 2, createdAtUtc: '2026-04-23T13:30:00Z', updatedAtUtc: '2026-04-23T13:33:02Z' },
  { traceId: '2d4b9ab1-0e11-42b6-81e7-5e2b69c7f4ab', workflowKey: 'support-triage',   workflowVersion: 14, currentState: 'Completed', currentAgentKey: 'pr-composer',         roundCount: 3, createdAtUtc: '2026-04-23T13:02:00Z', updatedAtUtc: '2026-04-23T13:08:55Z' },
  { traceId: 'a0cb4e76-22ad-4488-a4b1-4b5cc65dfcd0', workflowKey: 'pr-review',        workflowVersion: 8,  currentState: 'Running',   currentAgentKey: 'technical-reviewer',  roundCount: 2, createdAtUtc: '2026-04-23T12:47:00Z', updatedAtUtc: '2026-04-23T12:49:30Z', parentTraceId: '5fe381bb-9dd4-4e10-b3aa-ff1c1d9fe9c2' },
  { traceId: 'f31ab278-9e9a-4a22-b501-7ce98d41ff2e', workflowKey: 'spec-extract',     workflowVersion: 3,  currentState: 'Completed', currentAgentKey: 'spec-distiller',      roundCount: 1, createdAtUtc: '2026-04-23T12:19:00Z', updatedAtUtc: '2026-04-23T12:21:07Z' },
  { traceId: '7e42fa02-f8c1-469d-845c-1d3bdbb02215', workflowKey: 'code-review',      workflowVersion: 6,  currentState: 'Escalated', currentAgentKey: 'hitl-design-review',  roundCount: 4, createdAtUtc: '2026-04-23T11:55:00Z', updatedAtUtc: '2026-04-23T12:14:48Z' },
  { traceId: 'c5982113-3e55-4b77-9182-5b4d33c77d81', workflowKey: 'support-triage',   workflowVersion: 14, currentState: 'Completed', currentAgentKey: 'pr-composer',         roundCount: 3, createdAtUtc: '2026-04-23T11:20:00Z', updatedAtUtc: '2026-04-23T11:26:33Z' },
  { traceId: '8b1cdd44-af12-4d60-8e6e-9df9d12e6aa9', workflowKey: 'pr-review',        workflowVersion: 8,  currentState: 'Failed',    currentAgentKey: 'technical-reviewer',  roundCount: 1, createdAtUtc: '2026-04-23T10:47:00Z', updatedAtUtc: '2026-04-23T10:49:11Z' },
  { traceId: '45221a07-8845-4e3a-9d9d-0c8b99fc6f1a', workflowKey: 'support-triage',   workflowVersion: 14, currentState: 'Completed', currentAgentKey: 'pr-composer',         roundCount: 2, createdAtUtc: '2026-04-23T09:30:00Z', updatedAtUtc: '2026-04-23T09:34:02Z' }
];

const HITL_TASKS = [
  { id: 4821, traceId: '5fe381bb-9dd4-4e10-b3aa-ff1c1d9fe9c2', roundId: 'round-3', agentKey: 'hitl-compliance', agentVersion: 1, inputPreview: 'Deploy approval requested for branch release/2026.04.23 — changeset modifies persistence layer and affects 4 services. Risk level: MEDIUM.', createdAtUtc: '2026-04-23T14:09:00Z', state: 'Pending' },
  { id: 4819, traceId: '7e42fa02-f8c1-469d-845c-1d3bdbb02215', roundId: 'round-4', agentKey: 'hitl-design-review', agentVersion: 3, inputPreview: 'Design review for PR #2847 — new workflow inputs form. Please verify the JSON schema editor and Monaco integration meet accessibility standards before merge.', createdAtUtc: '2026-04-23T12:12:00Z', state: 'Pending' },
  { id: 4812, traceId: 'bb13cc4e-0001-4000-8000-000000000009', roundId: 'round-2', agentKey: 'hitl-compliance', agentVersion: 1, inputPreview: 'Bulk tenant migration plan. 1,240 records. Proposed rollout window: Thu 02:00–04:00 UTC.', createdAtUtc: '2026-04-23T08:41:00Z', state: 'Pending', subflowPath: ['tenant-ops'] }
];

const DLQ_QUEUES = [
  { queueName: 'codeflow.agent-runner_error', messageCount: 7 },
  { queueName: 'codeflow.orchestrator_error', messageCount: 2 },
  { queueName: 'codeflow.hitl-writer_error', messageCount: 0 },
  { queueName: 'codeflow.subflow-spawner_error', messageCount: 1 }
];

const DLQ_MESSAGES = [
  { messageId: '01HZ7Q1K3E8V9D0XYA4FKM23WR', queueName: 'codeflow.agent-runner_error', firstFaultedAtUtc: '2026-04-23T13:51:02Z', faultExceptionType: 'HttpRequestException', faultExceptionMessage: 'The SSL connection could not be established, see inner exception. Authentication failed because the remote party sent a TLS alert: BadCertificate.', originalInputAddress: 'rabbitmq://localhost/codeflow.agent-runner', payloadPreview: '{\n  "traceId": "9c884a3b-2f41-4f0c-b321-8cd1de773a80",\n  "roundId": "round-2",\n  "agentKey": "technical-reviewer",\n  "agentVersion": 4,\n  "inputRef": "s3://cf-artifacts/inputs/9c884a.json",\n  "attempt": 4\n}' },
  { messageId: '01HZ7Q0Y2M4T6A9XYA4FKM12XR', queueName: 'codeflow.agent-runner_error', firstFaultedAtUtc: '2026-04-23T13:22:44Z', faultExceptionType: 'RateLimitException', faultExceptionMessage: 'Provider returned HTTP 429. Retry-after: 60s. Budget exceeded after 4 attempts.', originalInputAddress: 'rabbitmq://localhost/codeflow.agent-runner', payloadPreview: '{ "traceId": "8b1cdd44-af12-4d60-8e6e-9df9d12e6aa9", "agentKey": "technical-reviewer" }' },
  { messageId: '01HZ7PZNS8X4V1YXXA4FKM09PP', queueName: 'codeflow.orchestrator_error', firstFaultedAtUtc: '2026-04-23T12:07:11Z', faultExceptionType: 'InvalidOperationException', faultExceptionMessage: 'Workflow version mismatch: saga pinned to v7, latest is v8. Rotation requires explicit opt-in.', originalInputAddress: 'rabbitmq://localhost/codeflow.orchestrator', payloadPreview: '{ "traceId": "c91d…", "pinnedVersion": 7, "latestVersion": 8 }' }
];

const MCP_SERVERS = [
  { id: 1, key: 'github-mcp', displayName: 'GitHub MCP', transport: 'StreamableHttp', endpointUrl: 'https://mcp.github.com/v1/sse', hasBearerToken: true, healthStatus: 'Healthy', lastVerifiedAtUtc: '2026-04-23T14:00:00Z' },
  { id: 2, key: 'supabase-mcp', displayName: 'Supabase MCP', transport: 'HttpSse', endpointUrl: 'https://mcp.supabase.co/stream', hasBearerToken: true, healthStatus: 'Healthy', lastVerifiedAtUtc: '2026-04-23T13:58:00Z' },
  { id: 3, key: 'notion-mcp', displayName: 'Notion MCP', transport: 'StreamableHttp', endpointUrl: 'https://api.notion.so/mcp', hasBearerToken: true, healthStatus: 'Unhealthy', lastVerifiedAtUtc: '2026-04-23T11:20:00Z', lastVerificationError: 'HTTP 401 Unauthorized' },
  { id: 4, key: 'local-files', displayName: 'Local filesystem', transport: 'StreamableHttp', endpointUrl: 'http://localhost:8081/mcp', hasBearerToken: false, healthStatus: 'Unverified', lastVerifiedAtUtc: null }
];

const ROLES = [
  { id: 1, key: 'reviewer', displayName: 'Reviewer', description: 'Read-only access to workflows and traces; may comment on HITL tasks.', grantCount: 6, skillCount: 2 },
  { id: 2, key: 'operator', displayName: 'Operator', description: 'Can trigger runs, terminate traces, and retry DLQ messages.', grantCount: 11, skillCount: 4 },
  { id: 3, key: 'admin', displayName: 'Admin', description: 'Full control over agents, workflows, settings, and role grants.', grantCount: 22, skillCount: 8 }
];

const SKILLS = [
  { id: 1, name: 'safe-ssh', body: 'Use keypair auth. Never interactive shells.', updatedAtUtc: '2026-04-20T10:00:00Z' },
  { id: 2, name: 'sql-read-only', body: 'SELECT-only. No DDL, no DML. Timeout 3s.', updatedAtUtc: '2026-04-18T15:00:00Z' },
  { id: 3, name: 'git-pr-hygiene', body: 'Conventional commits. Squash-merge. No force-push.', updatedAtUtc: '2026-04-14T09:20:00Z' },
  { id: 4, name: 'pii-redaction', body: 'Never log emails, phone numbers, or national IDs.', updatedAtUtc: '2026-04-11T17:41:00Z' }
];

// Workflow graph — the flagship "support-triage v14" shown on the canvas.
// layoutX/Y in canvas-space pixels.
const WORKFLOW = {
  key: 'support-triage',
  version: 14,
  name: 'Support triage',
  maxRoundsPerRound: 6,
  createdAtUtc: '2026-04-22T14:11:00Z',
  nodes: [
    { id: 'n_start',    kind: 'Start',      label: 'Start',                 x:  40, y: 220, outputPorts: ['default'] },
    { id: 'n_intake',   kind: 'Agent',      label: 'intake-router v7',      agentKey: 'intake-router',   agentVersion: 7, x: 240, y: 220, outputPorts: ['support','billing','code'] },
    { id: 'n_logic',    kind: 'Logic',      label: 'region split',          x: 500, y: 220, outputPorts: ['us','eu','apac'], hasScript: true },
    { id: 'n_triage',   kind: 'Agent',      label: 'triage-classifier v12', agentKey: 'triage-classifier', agentVersion: 12, x: 760, y:  90, outputPorts: ['p1','p2','p3'] },
    { id: 'n_reviewer', kind: 'Agent',      label: 'technical-reviewer v4', agentKey: 'technical-reviewer', agentVersion: 4, x: 760, y: 360, outputPorts: ['approve','changes','reject'] },
    { id: 'n_loop',     kind: 'ReviewLoop', label: 'review ×3',             reviewMaxRounds: 3, loopDecision: 'Rejected', x: 1040, y: 360, outputPorts: ['done','exhausted'] },
    { id: 'n_hitl',     kind: 'Hitl',       label: 'compliance-review',     agentKey: 'hitl-compliance', x: 1040, y: 90, outputPorts: ['approved','rejected'] },
    { id: 'n_compose',  kind: 'Agent',      label: 'pr-composer v5',        agentKey: 'pr-composer', agentVersion: 5, x: 1320, y: 220, outputPorts: ['default'] },
    { id: 'n_esc',      kind: 'Escalation', label: 'Escalate to humans',    x: 1320, y: 450, outputPorts: [] }
  ],
  edges: [
    { from: 'n_start',    fp: 'default',  to: 'n_intake',   tp: 'in' },
    { from: 'n_intake',   fp: 'support',  to: 'n_logic',    tp: 'in' },
    { from: 'n_intake',   fp: 'billing',  to: 'n_logic',    tp: 'in' },
    { from: 'n_intake',   fp: 'code',     to: 'n_logic',    tp: 'in' },
    { from: 'n_logic',    fp: 'us',       to: 'n_triage',   tp: 'in' },
    { from: 'n_logic',    fp: 'eu',       to: 'n_reviewer', tp: 'in' },
    { from: 'n_logic',    fp: 'apac',     to: 'n_reviewer', tp: 'in' },
    { from: 'n_triage',   fp: 'p1',       to: 'n_hitl',     tp: 'in' },
    { from: 'n_triage',   fp: 'p2',       to: 'n_compose',  tp: 'in' },
    { from: 'n_reviewer', fp: 'approve',  to: 'n_loop',     tp: 'in' },
    { from: 'n_reviewer', fp: 'changes',  to: 'n_loop',     tp: 'in' },
    { from: 'n_reviewer', fp: 'reject',   to: 'n_esc',      tp: 'in' },
    { from: 'n_loop',     fp: 'done',     to: 'n_compose',  tp: 'in' },
    { from: 'n_loop',     fp: 'exhausted',to: 'n_esc',      tp: 'in' },
    { from: 'n_hitl',     fp: 'approved', to: 'n_compose',  tp: 'in' },
    { from: 'n_hitl',     fp: 'rejected', to: 'n_esc',      tp: 'in' }
  ]
};

// Trace detail timeline for the first running trace
const TRACE_DETAIL_STEPS = [
  { id: 's1', state: 'ok',  agentKey: 'intake-router', agentVersion: 7, decision: 'Completed', port: 'support', when: '14:22:03',
    payload: '{\n  "category": "support",\n  "confidence": 0.94,\n  "language": "en"\n}' },
  { id: 's2', state: 'ok',  agentKey: 'logic: region-split', decision: 'Evaluated', port: 'us',   when: '14:22:03',
    logs: ['input.region === "us-east-1"', 'matched port: us'] },
  { id: 's3', state: 'ok',  agentKey: 'triage-classifier', agentVersion: 12, decision: 'Completed', port: 'p1', when: '14:22:38',
    payload: '{\n  "priority": "p1",\n  "category": "billing-dispute",\n  "reasoning": "Customer quoted Section 4.2 of the MSA and requested legal review."\n}' },
  { id: 's4', state: 'hitl',  agentKey: 'hitl-compliance', decision: 'Pending', when: '14:25:10',
    payload: 'Awaiting human review — routed to #compliance-review on Slack' },
  { id: 's5', state: 'run', agentKey: 'pr-composer', agentVersion: 5, decision: 'Running', when: '14:29:12' }
];

window.CF_DATA = { AGENTS, TRACES, HITL_TASKS, DLQ_QUEUES, DLQ_MESSAGES, MCP_SERVERS, ROLES, SKILLS, WORKFLOW, TRACE_DETAIL_STEPS };
