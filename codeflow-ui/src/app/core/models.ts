export type AgentDecisionKind = 'Completed' | 'Approved' | 'ApprovedWithActions' | 'Rejected' | 'Failed';

export interface AgentSummary {
  key: string;
  latestVersion: number;
  name?: string | null;
  provider?: string | null;
  model?: string | null;
  type: string;
  latestCreatedAtUtc: string;
  latestCreatedBy?: string | null;
}

export interface AgentVersionSummary {
  key: string;
  version: number;
  createdAtUtc: string;
  createdBy?: string | null;
}

export interface AgentVersion {
  key: string;
  version: number;
  type: string;
  config: AgentConfig | null;
  createdAtUtc: string;
  createdBy?: string | null;
}

export interface AgentConfig {
  type?: 'agent' | 'hitl';
  name?: string;
  description?: string;
  provider?: 'openai' | 'anthropic' | 'lmstudio';
  model?: string;
  systemPrompt?: string;
  promptTemplate?: string;
  allowedTools?: string[];
  maxTokens?: number;
  temperature?: number;
  enableHostTools?: boolean;
  [key: string]: unknown;
}

export interface WorkflowEdge {
  fromAgentKey: string;
  decision: AgentDecisionKind;
  discriminator?: unknown;
  toAgentKey: string;
  rotatesRound: boolean;
  sortOrder: number;
}

export interface WorkflowSummary {
  key: string;
  latestVersion: number;
  name: string;
  startAgentKey: string;
  escalationAgentKey?: string | null;
  edgeCount: number;
  createdAtUtc: string;
}

export interface WorkflowDetail {
  key: string;
  version: number;
  name: string;
  startAgentKey: string;
  escalationAgentKey?: string | null;
  maxRoundsPerRound: number;
  edges: WorkflowEdge[];
  createdAtUtc: string;
}

export interface TraceSummary {
  traceId: string;
  workflowKey: string;
  workflowVersion: number;
  currentState: string;
  currentAgentKey: string;
  roundCount: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface TraceDecision {
  agentKey: string;
  agentVersion: number;
  decision: AgentDecisionKind;
  decisionPayload?: unknown;
  roundId: string;
  recordedAtUtc: string;
}

export interface HitlTask {
  id: number;
  traceId: string;
  roundId: string;
  agentKey: string;
  agentVersion: number;
  inputRef: string;
  inputPreview?: string | null;
  createdAtUtc: string;
  state: 'Pending' | 'Decided' | 'Cancelled';
  decision?: AgentDecisionKind | null;
  decidedAtUtc?: string | null;
  deciderId?: string | null;
}

export interface TraceDetail {
  traceId: string;
  workflowKey: string;
  workflowVersion: number;
  currentState: string;
  currentAgentKey: string;
  currentRoundId: string;
  roundCount: number;
  pinnedAgentVersions: Record<string, number>;
  decisions: TraceDecision[];
  pendingHitl: HitlTask[];
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface CreateTraceRequest {
  workflowKey: string;
  workflowVersion?: number | null;
  input: string;
  inputFileName?: string;
}

export interface CreateTraceResponse {
  traceId: string;
}

export interface HitlDecisionRequest {
  decision: AgentDecisionKind;
  reason?: string;
  actions?: string[];
  reasons?: string[];
  outputText?: string;
}

export type TraceStreamEventKind = 'requested' | 'completed';

export interface TraceStreamEvent {
  traceId: string;
  roundId: string;
  kind: 'Requested' | 'Completed';
  agentKey: string;
  agentVersion: number;
  outputRef?: string | null;
  inputRef?: string | null;
  decision?: AgentDecisionKind | null;
  decisionPayload?: unknown;
  timestampUtc: string;
}
