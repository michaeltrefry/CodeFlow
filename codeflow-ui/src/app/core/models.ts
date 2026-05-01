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
  /** Read-only display value: the working-directory root the deployment is currently using. */
  workingDirectoryRoot: string;
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

export const LLM_PROVIDER_DISPLAY_NAMES: Record<LlmProviderKey, string> = {
  openai: 'OpenAI',
  anthropic: 'Anthropic',
  lmstudio: 'LM Studio (local)',
};

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

/**
 * HAA-15 — DB-backed admin defaults for the homepage AI assistant. Selects which configured
 * LLM provider/model the assistant uses on a fresh conversation; caps cumulative tokens per
 * conversation. Fields are nullable because each layer falls back to the previous: per-call
 * override → these admin defaults → appsettings → first listed model.
 */
export interface AssistantSettingsResponse {
  provider: LlmProviderKey | null;
  model: string | null;
  maxTokensPerConversation: number | null;
  /**
   * HAA-18 — optional agent role whose host + MCP tool grants are merged into the homepage
   * assistant's tool surface. Null means "built-in tools only".
   */
  assignedAgentRoleId: number | null;
  updatedBy: string | null;
  updatedAtUtc: string | null;
}

export interface AssistantSettingsWriteRequest {
  provider: LlmProviderKey | null;
  model: string | null;
  maxTokensPerConversation: number | null;
  assignedAgentRoleId: number | null;
}

// --- Notification subsystem (epic 48) ----------------------------------------------------

export type NotificationChannel = 'Email' | 'Sms' | 'Slack';

export const NOTIFICATION_CHANNELS: readonly NotificationChannel[] = ['Email', 'Sms', 'Slack'] as const;

export type NotificationEventKind = 'HitlTaskPending';

export const NOTIFICATION_EVENT_KINDS: readonly NotificationEventKind[] = ['HitlTaskPending'] as const;

export type NotificationSeverity = 'Info' | 'Normal' | 'High' | 'Urgent';

export const NOTIFICATION_SEVERITIES: readonly NotificationSeverity[] = ['Info', 'Normal', 'High', 'Urgent'] as const;

export type NotificationCredentialAction = 'Preserve' | 'Replace' | 'Clear';

export interface NotificationCredentialUpdateRequest {
  action: NotificationCredentialAction;
  value?: string | null;
}

export interface NotificationProviderResponse {
  id: string;
  displayName: string;
  channel: NotificationChannel;
  endpointUrl?: string | null;
  fromAddress?: string | null;
  hasCredential: boolean;
  additionalConfigJson?: string | null;
  enabled: boolean;
  isArchived: boolean;
  createdAtUtc: string;
  createdBy?: string | null;
  updatedAtUtc: string;
  updatedBy?: string | null;
}

export interface NotificationProviderWriteRequest {
  displayName: string;
  channel: NotificationChannel;
  endpointUrl?: string | null;
  fromAddress?: string | null;
  additionalConfigJson?: string | null;
  enabled: boolean;
  credential?: NotificationCredentialUpdateRequest;
}

export interface NotificationRecipientDto {
  channel: NotificationChannel;
  address: string;
  displayName?: string | null;
}

export interface NotificationTemplateRefDto {
  templateId: string;
  version: number;
}

export interface NotificationRouteResponse {
  routeId: string;
  eventKind: NotificationEventKind;
  providerId: string;
  recipients: NotificationRecipientDto[];
  template: NotificationTemplateRefDto;
  minimumSeverity: NotificationSeverity;
  enabled: boolean;
}

export interface NotificationRouteWriteRequest {
  eventKind: NotificationEventKind;
  providerId: string;
  recipients: NotificationRecipientDto[];
  template: NotificationTemplateRefDto;
  minimumSeverity: NotificationSeverity;
  enabled: boolean;
}

export interface NotificationTemplateResponse {
  templateId: string;
  version: number;
  eventKind: NotificationEventKind;
  channel: NotificationChannel;
  subjectTemplate?: string | null;
  bodyTemplate: string;
  createdAtUtc: string;
  createdBy?: string | null;
  updatedAtUtc: string;
  updatedBy?: string | null;
}

export interface NotificationDiagnosticsResponse {
  publicBaseUrl?: string | null;
  providerCount: number;
  routeCount: number;
  actionUrlsConfigured: boolean;
}

// sc-58 — validate + test-send DTOs.
export interface NotificationProviderValidationResponse {
  isValid: boolean;
  errorCode?: string | null;
  errorMessage?: string | null;
}

export interface NotificationTestSendRequest {
  recipient: NotificationRecipientDto;
  template?: NotificationTemplateRefDto | null;
}

export interface NotificationTestDeliveryDto {
  status: string;
  providerMessageId?: string | null;
  normalizedDestination?: string | null;
  errorCode?: string | null;
  errorMessage?: string | null;
}

export interface NotificationTestSendResponse {
  subject?: string | null;
  body: string;
  actionUrl: string;
  delivery: NotificationTestDeliveryDto;
}

// sc-59 — delivery audit listing.
export type NotificationDeliveryStatus = 'Unknown' | 'Sent' | 'Failed' | 'Skipped' | 'Retrying' | 'Suppressed';

export const NOTIFICATION_DELIVERY_STATUSES: readonly NotificationDeliveryStatus[] = [
  'Sent',
  'Failed',
  'Skipped',
  'Retrying',
  'Suppressed',
] as const;

export interface NotificationDeliveryAttemptResponse {
  id: number;
  eventId: string;
  eventKind: NotificationEventKind;
  routeId: string;
  providerId: string;
  status: NotificationDeliveryStatus;
  attemptNumber: number;
  attemptedAtUtc: string;
  completedAtUtc?: string | null;
  normalizedDestination: string;
  providerMessageId?: string | null;
  errorCode?: string | null;
  errorMessage?: string | null;
  createdAtUtc: string;
}

export interface NotificationDeliveryAttemptListResponse {
  items: NotificationDeliveryAttemptResponse[];
  nextBeforeId?: number | null;
}

export interface NotificationDeliveryAttemptListQuery {
  eventId?: string | null;
  providerId?: string | null;
  routeId?: string | null;
  status?: NotificationDeliveryStatus | null;
  sinceUtc?: string | null;
  beforeId?: number | null;
  limit?: number | null;
}

export type WorkflowNodeKind = 'Start' | 'Agent' | 'Logic' | 'Hitl' | 'Subflow' | 'ReviewLoop' | 'Transform' | 'Swarm';

export type WorkflowInputKind = 'Text' | 'Json';

export type WorkflowTransformOutputType = 'string' | 'json';

export type WorkflowSwarmProtocol = 'Sequential' | 'Coordinator';

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
  // Transform nodes only: the Scriban template body and how its rendered output is interpreted.
  template?: string | null;
  outputType?: WorkflowTransformOutputType;
  // Swarm nodes only. Both Sequential (sc-43) and Coordinator (sc-46) protocols are dispatchable.
  swarmProtocol?: WorkflowSwarmProtocol | null;
  swarmN?: number | null;
  contributorAgentKey?: string | null;
  contributorAgentVersion?: number | null;
  synthesizerAgentKey?: string | null;
  synthesizerAgentVersion?: number | null;
  coordinatorAgentKey?: string | null;
  coordinatorAgentVersion?: number | null;
  swarmTokenBudget?: number | null;
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

export interface TraceDescendant {
  summary: TraceSummary;
  detail: TraceDetail;
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
  /**
   * sc-273 — coarse classification of the verdict source so the trace timeline can
   * distinguish mechanical-gate decisions from model-side reviewer decisions.
   * - `'mechanical'`: agent has a host grant for `run_command` or `apply_patch` —
   *   decision was gated by deterministic command execution.
   * - `'model'`: agent has no host-tool grants — decision came from the LLM's response
   *   to its prompt.
   * - `null` / absent: agent has a mixed grant set; the timeline omits the badge.
   */
  verdictSource?: 'mechanical' | 'model' | null;
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

export type TraceStreamEventKind = 'requested' | 'completed' | 'tokenusagerecorded';

export interface TraceStreamEvent {
  traceId: string;
  roundId: string;
  kind: 'Requested' | 'Completed' | 'TokenUsageRecorded';
  agentKey: string;
  agentVersion: number;
  outputRef?: string | null;
  inputRef?: string | null;
  decision?: DecisionPortName | null;
  decisionPayload?: unknown;
  timestampUtc: string;
  /** Populated only when `kind === 'TokenUsageRecorded'`. Carries the persisted
   *  TokenUsageRecord — added by Token Usage Tracking slice 5. */
  tokenUsage?: TokenUsageEventPayload | null;
}

// ---------- Token Usage Tracking (epic 7ac46356) ----------

/** SSE-event payload mirroring `CodeFlow.Api/TraceEvents/TraceEvent.cs`'s
 *  `TokenUsageEventPayload`. The trace inspector merges these into the per-trace
 *  rollup signal so live overlays update incrementally during execution. */
export interface TokenUsageEventPayload {
  recordId: string;
  nodeId: string;
  invocationId: string;
  scopeChain: string[];
  provider: string;
  model: string;
  /** Provider-reported usage object verbatim (`input_tokens`, `output_tokens`,
   *  cache fields, reasoning fields, etc.). Schema-less by design. */
  usage: Record<string, unknown>;
}

/** Response shape of `GET /api/traces/{id}/token-usage`. All rollups are
 *  computed on-read from the raw `TokenUsageRecord` rows — see
 *  `CodeFlow.Api/TokenTracking/TokenUsageAggregator.cs`. */
export interface TraceTokenUsageDto {
  traceId: string;
  /** HAA-14 — `'workflow'` for saga-driven traces; `'assistant'` for synthetic
   *  traces minted per assistant conversation. The token panel renders the
   *  appropriate stream label based on this flag; default to `'workflow'` for
   *  backward compatibility if a server response omits it. */
  streamKind: 'workflow' | 'assistant';
  total: TokenUsageRollup;
  records: TokenUsageRecordDto[];
  byInvocation: TokenUsageInvocationRollup[];
  byNode: TokenUsageNodeRollup[];
  byScope: TokenUsageScopeRollup[];
}

export interface TokenUsageRecordDto {
  recordId: string;
  nodeId: string;
  invocationId: string;
  scopeChain: string[];
  provider: string;
  model: string;
  recordedAtUtc: string;
  usage: Record<string, unknown>;
  /** This single record's flattened totals (numeric leaves keyed by dotted JSON path). */
  totals: Record<string, number>;
}

export interface TokenUsageRollup {
  callCount: number;
  /** Flattened sum of every numeric leaf across the rollup's records, keyed by
   *  dotted JSON path (e.g. `output_tokens_details.reasoning_tokens`). */
  totals: Record<string, number>;
  /** Per-(provider, model) breakdown. Always populated, even when the rollup
   *  spans only one combo, so the UI doesn't have to special-case. */
  byProviderModel: TokenUsageProviderModelTotals[];
}

export interface TokenUsageProviderModelTotals {
  provider: string;
  model: string;
  totals: Record<string, number>;
}

export interface TokenUsageInvocationRollup {
  nodeId: string;
  invocationId: string;
  rollup: TokenUsageRollup;
}

export interface TokenUsageNodeRollup {
  nodeId: string;
  rollup: TokenUsageRollup;
}

export interface TokenUsageScopeRollup {
  scopeId: string;
  rollup: TokenUsageRollup;
}

// ---------- Replay-with-edit (T2) ----------

export interface ReplayEdit {
  agentKey: string;
  ordinal: number;
  decision?: string | null;
  output?: string | null;
  payload?: unknown;
}

export interface ReplayMockResponse {
  decision: string;
  output?: string | null;
  payload?: unknown;
}

export interface ReplayRequest {
  edits?: ReplayEdit[];
  additionalMocks?: Record<string, ReplayMockResponse[]>;
  workflowVersionOverride?: number | null;
  force?: boolean;
}

export type ReplayDriftLevel = 'None' | 'Soft' | 'Hard';

export interface ReplayDrift {
  level: ReplayDriftLevel;
  warnings: string[];
}

export interface ReplayExhaustedAgent {
  agentKey: string;
  recordedResponses: number;
}

export interface RecordedDecisionRef {
  agentKey: string;
  ordinalPerAgent: number;
  sagaCorrelationId: string;
  sagaOrdinal: number;
  nodeId: string | null;
  roundId: string;
  originalDecision: string;
}

export interface ReplayEvent {
  ordinal: number;
  kind: string;
  nodeId: string;
  nodeKind: string;
  agentKey?: string | null;
  portName?: string | null;
  message?: string | null;
  inputPreview?: string | null;
  outputPreview?: string | null;
  reviewRound?: number | null;
  maxRounds?: number | null;
  subflowDepth?: number | null;
  subflowKey?: string | null;
  subflowVersion?: number | null;
  logs?: string[] | null;
  decisionPayload?: unknown;
}

export interface ReplayHitlPayload {
  nodeId: string;
  agentKey: string;
  input?: string | null;
  outputTemplate?: string | null;
  decisionOutputTemplates?: Record<string, string> | null;
  renderedFormPreview?: string | null;
  renderError?: string | null;
}

export type ReplayState = 'Completed' | 'HitlReached' | 'Failed' | 'StepLimitExceeded' | 'DriftRefused';

export interface ReplayResponse {
  originalTraceId: string;
  replayState: ReplayState;
  replayTerminalPort: string | null;
  failureCode: 'queue_exhausted' | 'drift_hard_refused' | null;
  failureReason: string | null;
  exhaustedAgent: ReplayExhaustedAgent | null;
  decisions: RecordedDecisionRef[];
  replayEvents: ReplayEvent[];
  hitlPayload: ReplayHitlPayload | null;
  drift: ReplayDrift;
  /** sc-275: lineage metadata for the just-recorded replay attempt. Null when the
   *  endpoint pre-dates sc-275 (older deployments) — UI guards on the missing case. */
  lineage: ReplayLineage | null;
}

export interface ReplayLineage {
  lineageId: string;
  contentHash: string;
  parentTraceId: string;
  generation: number;
  createdAtUtc: string;
  reason: string | null;
}

// sc-274 phase 1: ambiguity preflight refusal payload (HTTP 422 from POST /api/traces/{id}/replay
// when the deterministic assessor decides the edits don't meet the mode's clarity threshold).
// Surfaced inline in the replay panel as a clarification banner.

export interface PreflightDimension {
  dimension: string;
  score: number;
  reason: string | null;
}

export interface PreflightRefusalResponse {
  originalTraceId: string;
  code: 'preflight-ambiguous';
  mode: string;
  overallScore: number;
  threshold: number;
  dimensions: PreflightDimension[];
  missingFields: string[];
  clarificationQuestions: string[];
}

// sc-271 PR2: trace evidence bundle manifest, mirroring CodeFlow.Api.TraceBundle types.
// The trace inspector renders a composition summary from this without parsing the zip.

/** Pointer from a manifest field to a deduplicated artifact under `artifacts/` in the zip.
 *  An empty `sha256` + zero `sizeBytes` means the artifact was missing from the store at
 *  export time (a "dangling pointer"); the manifest still records the original ref so a
 *  retention-driven gap is visible in the bundle instead of silently dropped. */
export interface TraceBundleArtifactPointer {
  originalRef: string;
  sha256: string;
  sizeBytes: number;
  bundlePath: string;
}

export interface TraceBundleArtifactRef {
  bundlePath: string;
  sha256: string;
  sizeBytes: number;
  contentType: string | null;
  originalRef: string;
}

export interface TraceBundleSagaSummary {
  correlationId: string;
  traceId: string;
  parentTraceId: string | null;
  subflowDepth: number;
  workflowKey: string;
  workflowVersion: number;
  currentState: string;
  failureReason: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  pinnedAgentVersions: Record<string, number>;
}

export interface TraceBundleDecision {
  sagaCorrelationId: string;
  ordinal: number;
  traceId: string;
  agentKey: string;
  agentVersion: number;
  decision: string;
  decisionPayloadJson: string | null;
  roundId: string;
  recordedAtUtc: string;
  nodeId: string | null;
  outputPortName: string | null;
  nodeEnteredAtUtc: string | null;
  input: TraceBundleArtifactPointer | null;
  output: TraceBundleArtifactPointer | null;
}

export interface TraceBundleRefusal {
  id: string;
  traceId: string | null;
  assistantConversationId: string | null;
  stage: string;
  code: string;
  reason: string;
  axis: string | null;
  path: string | null;
  detailJson: string | null;
  occurredAtUtc: string;
}

export interface TraceBundleAuthoritySnapshot {
  id: string;
  traceId: string;
  roundId: string;
  agentKey: string;
  agentVersion: number | null;
  workflowKey: string | null;
  workflowVersion: number | null;
  envelopeJson: string;
  blockedAxesJson: string;
  tiersJson: string;
  resolvedAtUtc: string;
}

export interface TraceBundleTokenUsageRecord {
  id: string;
  traceId: string;
  nodeId: string;
  invocationId: string;
  provider: string;
  model: string;
  recordedAtUtc: string;
  usageJson: string;
}

export interface TraceBundleTokenUsageSummary {
  recordCount: number;
  records: TraceBundleTokenUsageRecord[];
}

export interface TraceBundleReplayAttempt {
  id: string;
  parentTraceId: string;
  lineageId: string;
  contentHash: string;
  generation: number;
  replayState: string;
  terminalPort: string | null;
  driftLevel: string;
  reason: string | null;
  createdAtUtc: string;
}

export interface TraceBundleTraceSummary {
  traceId: string;
  rootSaga: TraceBundleSagaSummary;
  subflowSagas: TraceBundleSagaSummary[];
  decisions: TraceBundleDecision[];
  refusals: TraceBundleRefusal[];
  authoritySnapshots: TraceBundleAuthoritySnapshot[];
  tokenUsage: TraceBundleTokenUsageSummary;
  /** sc-275: replay-with-edit attempts rooted at this trace, ordered by createdAtUtc.
   *  Optional — older bundles produced before sc-275 don't carry this field. */
  replayAttempts?: TraceBundleReplayAttempt[];
}

export interface TraceBundleManifest {
  schemaVersion: string;
  generatedAtUtc: string;
  trace: TraceBundleTraceSummary;
  artifacts: TraceBundleArtifactRef[];
}
