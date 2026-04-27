/**
 * Author-defined output port name. Built-in ports include 'Completed', 'Approved',
 * 'Rejected', 'Failed', 'Exhausted', but any string an agent declares is valid.
 */
export type DecisionPortName = string;

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
  decisionOutputTemplates?: Record<string, string>;
  [key: string]: unknown;
}

export type DecisionOutputTemplateMode = 'llm' | 'hitl';

export interface DecisionOutputTemplatePreviewRequest {
  template: string;
  mode: DecisionOutputTemplateMode;
  decision?: string;
  outputPortName?: string;
  output?: string;
  input?: unknown;
  fieldValues?: Record<string, unknown>;
  context?: Record<string, unknown>;
  workflow?: Record<string, unknown>;
  reason?: string;
  reasons?: string[];
  actions?: string[];
}

export interface DecisionOutputTemplatePreviewResponse {
  rendered: string;
}

export interface DecisionOutputTemplatePreviewError {
  error: string;
}

export interface PromptPartialPinDto {
  key: string;
  version: number;
}

export interface PromptTemplatePreviewRequest {
  systemPrompt?: string | null;
  promptTemplate?: string | null;
  workflow?: Record<string, unknown>;
  context?: Record<string, unknown>;
  input?: string | null;
  reviewRound?: number | null;
  reviewMaxRounds?: number | null;
  optOutLastRoundReminder?: boolean;
  partialPins?: PromptPartialPinDto[];
}

export interface PromptTemplatePreviewAutoInjection {
  key: string;
  renderedBody: string;
  reason: string;
}

export interface PromptTemplatePreviewMissingPartial {
  key: string;
  version: number;
}

export interface PromptTemplatePreviewResponse {
  renderedSystemPrompt: string | null;
  renderedPromptTemplate: string | null;
  autoInjections: PromptTemplatePreviewAutoInjection[];
  missingPartials: PromptTemplatePreviewMissingPartial[];
}

export interface PromptTemplatePreviewError {
  error: string;
  missingPartials?: PromptTemplatePreviewMissingPartial[];
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
  workingDirectoryRoot?: string | null;
  workingDirectoryMaxAgeDays?: number | null;
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
  workingDirectoryRoot?: string | null;
  workingDirectoryMaxAgeDays?: number | null;
  token: GitHostTokenUpdateRequest;
}

export interface GitHostVerifyResponse {
  success: boolean;
  lastVerifiedAtUtc?: string | null;
  error?: string | null;
}

export type LlmProviderKey = 'openai' | 'anthropic' | 'lmstudio';

export const LLM_PROVIDER_KEYS: readonly LlmProviderKey[] = ['openai', 'anthropic', 'lmstudio'] as const;

export type LlmProviderTokenAction = 'Preserve' | 'Replace' | 'Clear';

export interface LlmProviderTokenUpdateRequest {
  action: LlmProviderTokenAction;
  value?: string | null;
}

export interface LlmProviderResponse {
  provider: LlmProviderKey;
  hasApiKey: boolean;
  endpointUrl?: string | null;
  apiVersion?: string | null;
  models: string[];
  updatedBy?: string | null;
  updatedAtUtc?: string | null;
}

export interface LlmProviderWriteRequest {
  endpointUrl?: string | null;
  apiVersion?: string | null;
  models?: string[];
  token?: LlmProviderTokenUpdateRequest;
}

export interface LlmProviderModelOption {
  provider: LlmProviderKey;
  model: string;
}

export type WorkflowNodeKind = 'Start' | 'Agent' | 'Logic' | 'Hitl' | 'Subflow' | 'ReviewLoop';

export type WorkflowInputKind = 'Text' | 'Json';

export interface WorkflowNode {
  id: string;
  kind: WorkflowNodeKind;
  agentKey?: string | null;
  agentVersion?: number | null;
  outputScript?: string | null;
  inputScript?: string | null;
  outputPorts: string[];
  layoutX: number;
  layoutY: number;
  subflowKey?: string | null;
  subflowVersion?: number | null;
  reviewMaxRounds?: number | null;
  loopDecision?: string | null;
}

export interface WorkflowEdge {
  fromNodeId: string;
  fromPort: string;
  toNodeId: string;
  toPort: string;
  rotatesRound: boolean;
  sortOrder: number;
  intentionalBackedge?: boolean;
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

export type WorkflowCategory = 'Workflow' | 'Subflow' | 'Loop';

export const WORKFLOW_CATEGORIES: readonly WorkflowCategory[] = ['Workflow', 'Subflow', 'Loop'] as const;

export const MAX_WORKFLOW_TAGS = 5;

export interface WorkflowSummary {
  key: string;
  latestVersion: number;
  name: string;
  category: WorkflowCategory;
  tags: string[];
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
  category: WorkflowCategory;
  tags: string[];
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
  /** Non-null when this saga was spawned as a subflow child of another trace. */
  parentTraceId?: string | null;
  /** Id of the parent Subflow or ReviewLoop node that spawned this child saga. */
  parentNodeId?: string | null;
  /** 1-indexed round number when this saga was spawned by a ReviewLoop parent. Null for plain Subflow children and top-level sagas. */
  parentReviewRound?: number | null;
  /** Snapshot of the parent ReviewLoop's MaxRounds at spawn. Null for plain Subflow children and top-level sagas. */
  parentReviewMaxRounds?: number | null;
}

export interface TraceDecision {
  agentKey: string;
  agentVersion: number;
  decision: DecisionPortName;
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
  decision?: DecisionPortName | null;
  decidedAtUtc?: string | null;
  deciderId?: string | null;
  /** Set when this HITL lives on a descendant saga — the trace that actually owns it. */
  originTraceId?: string | null;
  /** Ordered list of workflow keys from the immediate child of the root down to the owning saga. Empty for root-owned HITLs. */
  subflowPath?: string[] | null;
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
  outputPortName: string;
  reason?: string;
  actions?: string[];
  reasons?: string[];
  outputText?: string;
  fieldValues?: Record<string, unknown>;
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
  decision?: DecisionPortName | null;
  decisionPayload?: unknown;
  timestampUtc: string;
}
