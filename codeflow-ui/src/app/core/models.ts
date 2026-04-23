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
  isRetired: boolean;
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
  isRetired: boolean;
}

export interface AgentOutputDeclaration {
  kind: string;
  description?: string | null;
  payloadExample?: unknown;
}

export interface AgentConfig {
  type?: 'agent' | 'hitl';
  name?: string;
  description?: string;
  provider?: 'openai' | 'anthropic' | 'lmstudio';
  model?: string;
  systemPrompt?: string;
  promptTemplate?: string;
  outputTemplate?: string;
  maxTokens?: number;
  temperature?: number;
  outputs?: AgentOutputDeclaration[];
  [key: string]: unknown;
}

export type McpTransportKind = 'StreamableHttp' | 'HttpSse';
export type McpServerHealthStatus = 'Unverified' | 'Healthy' | 'Unhealthy';
export type BearerTokenAction = 'Preserve' | 'Clear' | 'Replace';

export interface McpServer {
  id: number;
  key: string;
  displayName: string;
  transport: McpTransportKind;
  endpointUrl: string;
  hasBearerToken: boolean;
  healthStatus: McpServerHealthStatus;
  lastVerifiedAtUtc?: string | null;
  lastVerificationError?: string | null;
  createdAtUtc: string;
  createdBy?: string | null;
  updatedAtUtc: string;
  updatedBy?: string | null;
  isArchived: boolean;
}

export interface McpServerTool {
  id: number;
  serverId: number;
  toolName: string;
  description?: string | null;
  parameters?: unknown;
  isMutating: boolean;
  syncedAtUtc: string;
}

export interface McpServerCreateRequest {
  key: string;
  displayName: string;
  transport: McpTransportKind;
  endpointUrl: string;
  bearerToken?: string | null;
}

export interface BearerTokenPayload {
  action: BearerTokenAction;
  value?: string;
}

export interface McpServerUpdateRequest {
  displayName: string;
  transport: McpTransportKind;
  endpointUrl: string;
  bearerToken: BearerTokenPayload;
}

export interface McpServerVerifyResponse {
  healthStatus: McpServerHealthStatus;
  lastVerifiedAtUtc?: string | null;
  lastVerificationError?: string | null;
  discoveredToolCount?: number | null;
}

export interface McpServerRefreshResponse {
  healthStatus: McpServerHealthStatus;
  lastVerifiedAtUtc?: string | null;
  lastVerificationError?: string | null;
  tools: McpServerTool[];
}

export type AgentRoleToolCategory = 'Host' | 'Mcp';

export interface AgentRole {
  id: number;
  key: string;
  displayName: string;
  description?: string | null;
  createdAtUtc: string;
  createdBy?: string | null;
  updatedAtUtc: string;
  updatedBy?: string | null;
  isArchived: boolean;
}

export interface AgentRoleGrant {
  category: AgentRoleToolCategory;
  toolIdentifier: string;
}

export interface AgentRoleCreateRequest {
  key: string;
  displayName: string;
  description?: string | null;
}

export interface AgentRoleUpdateRequest {
  displayName: string;
  description?: string | null;
}

export interface Skill {
  id: number;
  name: string;
  body: string;
  createdAtUtc: string;
  createdBy?: string | null;
  updatedAtUtc: string;
  updatedBy?: string | null;
  isArchived: boolean;
}

export interface SkillCreateRequest {
  name: string;
  body: string;
}

export interface SkillUpdateRequest {
  name: string;
  body: string;
}

export interface AgentRoleSkillGrants {
  skillIds: number[];
}

export interface HostTool {
  name: string;
  description: string;
  parameters?: unknown;
  isMutating: boolean;
}

export type GitHostMode = 'GitHub' | 'GitLab';

export interface GitHostSettingsResponse {
  mode: GitHostMode;
  baseUrl?: string | null;
  hasToken: boolean;
  lastVerifiedAtUtc?: string | null;
  updatedBy?: string | null;
  updatedAtUtc?: string | null;
}

export type GitHostTokenAction = 'Preserve' | 'Replace';

export interface GitHostTokenUpdateRequest {
  action: GitHostTokenAction;
  value?: string | null;
}

export interface GitHostSettingsRequest {
  mode: GitHostMode;
  baseUrl?: string | null;
  token: GitHostTokenUpdateRequest;
}

export interface GitHostVerifyResponse {
  success: boolean;
  lastVerifiedAtUtc?: string | null;
  error?: string | null;
}

export type WorkflowNodeKind = 'Start' | 'Agent' | 'Logic' | 'Hitl' | 'Escalation';

export type WorkflowInputKind = 'Text' | 'Json';

export interface WorkflowNode {
  id: string;
  kind: WorkflowNodeKind;
  agentKey?: string | null;
  agentVersion?: number | null;
  script?: string | null;
  outputPorts: string[];
  layoutX: number;
  layoutY: number;
}

export interface WorkflowEdge {
  fromNodeId: string;
  fromPort: string;
  toNodeId: string;
  toPort: string;
  rotatesRound: boolean;
  sortOrder: number;
}

export interface WorkflowInput {
  key: string;
  displayName: string;
  kind: WorkflowInputKind;
  required: boolean;
  defaultValueJson?: string | null;
  description?: string | null;
  ordinal: number;
}

export interface WorkflowSummary {
  key: string;
  latestVersion: number;
  name: string;
  nodeCount: number;
  edgeCount: number;
  inputCount: number;
  createdAtUtc: string;
}

export interface WorkflowDetail {
  key: string;
  version: number;
  name: string;
  maxRoundsPerRound: number;
  createdAtUtc: string;
  nodes: WorkflowNode[];
  edges: WorkflowEdge[];
  inputs: WorkflowInput[];
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
  nodeId?: string | null;
  outputPortName?: string | null;
  inputRef?: string | null;
  outputRef?: string | null;
}

export interface TraceLogicEvaluation {
  nodeId: string;
  outputPortName?: string | null;
  roundId: string;
  duration: string;
  logs: string[];
  failureKind?: string | null;
  failureMessage?: string | null;
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
  contextInputs: Record<string, unknown>;
  decisions: TraceDecision[];
  logicEvaluations: TraceLogicEvaluation[];
  pendingHitl: HitlTask[];
  createdAtUtc: string;
  updatedAtUtc: string;
  failureReason?: string | null;
}

export interface CreateTraceRequest {
  workflowKey: string;
  workflowVersion?: number | null;
  input: string;
  inputFileName?: string;
  inputs?: Record<string, unknown>;
}

export interface CreateTraceResponse {
  traceId: string;
}

export interface BulkDeleteTracesRequest {
  state?: string | null;
  olderThanDays: number;
}

export interface BulkDeleteTracesResponse {
  deletedCount: number;
}

export interface HitlDecisionRequest {
  decision: AgentDecisionKind;
  outputPortName?: string;
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
