import { HttpClient, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { WorkflowCategory, WorkflowDetail, WorkflowEdge, WorkflowInput, WorkflowNode, WorkflowSummary } from './models';

export interface WorkflowPayload {
  key?: string;
  name: string;
  maxRoundsPerRound: number;
  category: WorkflowCategory;
  tags: string[];
  nodes: WorkflowNode[];
  edges: WorkflowEdge[];
  inputs: WorkflowInput[];
}

export type ValidateScriptDirection = 'Input' | 'Output';

export interface ValidateScriptRequest {
  script: string;
  direction?: ValidateScriptDirection;
}

export interface ValidateScriptError {
  line: number;
  column: number;
  message: string;
}

export interface ValidateScriptResponse {
  ok: boolean;
  errors: ValidateScriptError[];
}

/** sc-395: `Refused` is a hard apply-blocker (just like `Conflict`) emitted when a user-supplied
 *  resolution fails a structural check — today only "UseExisting on an agent whose library
 *  max-version doesn't declare every output port the package's nodes route to." */
export type WorkflowPackageImportAction = 'Create' | 'Reuse' | 'Conflict' | 'Refused';
export type WorkflowPackageImportResourceKind =
  | 'Workflow'
  | 'Agent'
  | 'AgentRoleAssignment'
  | 'Role'
  | 'Skill'
  | 'McpServer';

/** sc-394: which of the three deterministic resolutions to apply on a Conflict row. */
export type WorkflowPackageImportResolutionMode = 'UseExisting' | 'Bump' | 'Copy';

/** sc-395: per-conflict resolution carried over the wire to /package/preview, /package/apply,
 *  and /package/apply-from-draft. Mirrors the server's `WorkflowPackageImportResolutionRequest`.
 *  `expectedExistingMaxVersion` is the value the client saw in the preview's `existingMaxVersion`
 *  when it picked this resolution; the apply endpoint re-reads the live max and 409s if they
 *  differ unless the request also carries `acknowledgeDrift: true`. */
export interface WorkflowPackageImportResolution {
  kind: WorkflowPackageImportResourceKind;
  key: string;
  /** Required for Workflow / Agent (the versioned kinds). Null for Role / Skill / McpServer / AgentRoleAssignment. */
  sourceVersion: number | null;
  mode: WorkflowPackageImportResolutionMode;
  /** Required when `mode === 'Copy'`. Must be unique per kind across the resolutions list. */
  newKey?: string | null;
  /** Optional but strongly recommended on Bump / UseExisting — drives drift-ack on apply. */
  expectedExistingMaxVersion?: number | null;
}

/** sc-395: 409 body returned by /package/apply or /package/apply-from-draft when one or more
 *  resolved entities have moved between preview and apply. The imports page surfaces the moved
 *  rows, prompts the user to re-resolve, and re-submits with `acknowledgeDrift: true`. */
export interface WorkflowPackageImportDriftConflict {
  error: string;
  movedEntities: WorkflowPackageImportDriftEntry[];
}

export interface WorkflowPackageImportDriftEntry {
  kind: WorkflowPackageImportResourceKind;
  key: string;
  sourceVersion: number | null;
  expectedExistingMaxVersion: number | null;
  currentExistingMaxVersion: number | null;
}

export interface WorkflowPackageReference {
  key: string;
  version: number;
}

export interface WorkflowPackageImportItem {
  kind: WorkflowPackageImportResourceKind;
  key: string;
  /** Post-rewrite target version. For an auto-bump Create row this is the bumped value, NOT
   *  the package's source version — use `sourceVersion` for that. */
  version?: number | null;
  action: WorkflowPackageImportAction;
  message: string;
  /** sc-393: the version the package itself carries, before any importer rewrite. Equals
   *  `version` for plain Create / Reuse rows. Differs from `version` for the auto-bump path.
   *  Null on non-versioned kinds (Role, Skill, McpServer, AgentRoleAssignment). */
  sourceVersion?: number | null;
  /** sc-393: the highest version present in the local library for this key, or null when none
   *  exists yet (or for non-versioned kinds). For a stale-package Conflict, this is the value
   *  the package would need to bump above to land cleanly. */
  existingMaxVersion?: number | null;
}

export interface WorkflowPackageImportPreview {
  entryPoint: WorkflowPackageReference;
  items: WorkflowPackageImportItem[];
  warnings: string[];
  createCount: number;
  reuseCount: number;
  conflictCount: number;
  /** sc-395: rows refused by a structural check on a user-supplied resolution. Hard apply-blocker. */
  refusedCount: number;
  warningCount: number;
  canApply: boolean;
}

/** V8 manifest — flat enumeration of every (key, version) the package includes.
 *  E5 surfaces this in the export-preview dialog before the author downloads. */
export interface WorkflowPackageManifest {
  workflows: WorkflowPackageReference[];
  agents: WorkflowPackageReference[];
  roles: string[];
  skills: string[];
  mcpServers: string[];
}

export interface WorkflowPackageMetadata {
  exportedFrom: string;
  exportedAtUtc: string;
}

export interface WorkflowPackageDocument {
  schemaVersion: string;
  metadata: WorkflowPackageMetadata;
  entryPoint: WorkflowPackageReference;
  workflows: { key: string; version: number; name: string }[];
  agents: { key: string; version: number; kind?: string }[];
  agentRoleAssignments: { agentKey: string; roleKeys: string[] }[];
  roles: { key: string; displayName: string }[];
  skills: { name: string }[];
  mcpServers: { key: string; displayName: string }[];
  manifest?: WorkflowPackageManifest;
}

export interface WorkflowPackageImportApplyResult {
  entryPoint: WorkflowPackageReference;
  items: WorkflowPackageImportItem[];
  warnings: string[];
  createCount: number;
  reuseCount: number;
  conflictCount: number;
  warningCount: number;
}

/** F2 dataflow snapshot — per-node static-analysis describing what's in scope when each node
 *  runs. Drives VZ1 (data-flow inspector), VZ2 (workflow-vars declaration), and E1 (script
 *  IntelliSense narrowing). Snapshot is computed from the SAVED workflow version, so reflects
 *  on-disk state, not the live editor draft. */
export type DataflowConfidence = 'Definite' | 'Conditional';

export interface DataflowVariableSource {
  nodeId: string;
  scriptKind: string;
}

export interface DataflowVariable {
  key: string;
  confidence: DataflowConfidence;
  sources: DataflowVariableSource[];
}

export interface DataflowInputSource {
  nodeId: string;
  port: string;
}

export interface DataflowLoopBindings {
  staticRound: number | null;
  maxRounds: number;
}

export interface NodeDataflowScope {
  nodeId: string;
  workflowVariables: DataflowVariable[];
  contextKeys: DataflowVariable[];
  inputSource: DataflowInputSource | null;
  loopBindings: DataflowLoopBindings | null;
}

export interface WorkflowDataflowDiagnostic {
  nodeId: string;
  scriptKind: string;
  message: string;
}

export interface WorkflowDataflowSnapshot {
  workflowKey: string;
  workflowVersion: number;
  scopesByNode: Record<string, NodeDataflowScope>;
  diagnostics: WorkflowDataflowDiagnostic[];
}

/** TN-6: live preview of a Transform node's Scriban template against a sample fixture.
 *  Mirrors the saga's render scope (`input.* + context.* + workflow.*`). When `outputType`
 *  is `'json'`, the rendered text is also JSON-parsed server-side and either `parsed` or
 *  `jsonParseError` is populated; for `'string'`, only `rendered` is populated. */
export type TransformPreviewOutputType = 'string' | 'json';

export interface TransformPreviewRequest {
  template: string;
  outputType: TransformPreviewOutputType;
  input?: unknown;
  context?: Record<string, unknown> | null;
  workflow?: Record<string, unknown> | null;
}

export interface TransformPreviewResponse {
  rendered: string;
  parsed?: unknown;
  jsonParseError?: string | null;
}

export interface TransformPreviewErrorResponse {
  error: string;
}

@Injectable({ providedIn: 'root' })
export class WorkflowsApi {
  private readonly http = inject(HttpClient);

  list(): Observable<WorkflowSummary[]> {
    return this.http.get<WorkflowSummary[]>('/api/workflows');
  }

  /**
   * HAA-14 — Workflows ordered by most recent saga activity for the homepage rail. Backend
   * filters out workflows that have never been run, so callers can render an empty state
   * confidently when the user is brand-new.
   */
  listRecent(take = 5): Observable<RecentWorkflow[]> {
    return this.http.get<RecentWorkflow[]>('/api/workflows/recent', { params: { take: String(take) } });
  }

  getLatest(key: string): Observable<WorkflowDetail> {
    return this.http.get<WorkflowDetail>(`/api/workflows/${encodeURIComponent(key)}`);
  }

  getVersion(key: string, version: number): Observable<WorkflowDetail> {
    return this.http.get<WorkflowDetail>(`/api/workflows/${encodeURIComponent(key)}/${version}`);
  }

  /** Returns the set of port names by which the given workflow version exits — used by the
   *  editor to populate Subflow/ReviewLoop port pickers without loading the full graph. */
  getTerminalPorts(key: string, version: number | null): Observable<string[]> {
    const url = version
      ? `/api/workflows/${encodeURIComponent(key)}/${version}/terminal-ports`
      : `/api/workflows/${encodeURIComponent(key)}/latest/terminal-ports`;
    return this.http.get<string[]>(url);
  }

  listVersions(key: string): Observable<WorkflowDetail[]> {
    return this.http.get<WorkflowDetail[]>(`/api/workflows/${encodeURIComponent(key)}/versions`);
  }

  downloadPackage(key: string, version: number): Observable<HttpResponse<Blob>> {
    return this.http.get(`/api/workflows/${encodeURIComponent(key)}/${version}/package`, {
      observe: 'response',
      responseType: 'blob'
    });
  }

  /** E5 / R5.5: fetch the parsed package JSON for the export-preview dialog. Same endpoint
   *  as downloadPackage; the response body is the entire serialized package including the
   *  V8 manifest. We surface counts and the manifest tree before letting the author save. */
  getPackage(key: string, version: number): Observable<HttpResponse<WorkflowPackageDocument>> {
    return this.http.get<WorkflowPackageDocument>(
      `/api/workflows/${encodeURIComponent(key)}/${version}/package`,
      { observe: 'response' }
    );
  }

  /** sc-395: optional `resolutions` lets the imports page (CR-4) drive a per-Conflict
   *  preview re-run with user-chosen Bump / UseExisting / Copy choices. Wire shape is now
   *  `{ package, resolutions }` so the server can extend the body with future fields without
   *  breaking the bare-package contract. */
  previewPackageImport(
    workflowPackage: unknown,
    resolutions?: WorkflowPackageImportResolution[],
  ): Observable<WorkflowPackageImportPreview> {
    return this.http.post<WorkflowPackageImportPreview>(
      '/api/workflows/package/preview',
      { package: workflowPackage, resolutions },
    );
  }

  /** sc-395: `acknowledgeDrift` is required (true) when retrying an apply that previously
   *  returned 409 (`WorkflowPackageImportDriftConflict`) — the server re-checks live max
   *  versions and accepts the apply against the moved values. */
  applyPackageImport(
    workflowPackage: unknown,
    resolutions?: WorkflowPackageImportResolution[],
    acknowledgeDrift?: boolean,
  ): Observable<WorkflowPackageImportApplyResult> {
    return this.http.post<WorkflowPackageImportApplyResult>(
      '/api/workflows/package/apply',
      { package: workflowPackage, resolutions, acknowledgeDrift },
    );
  }

  /**
   * Apply the package snapshot the assistant minted when it validated a workspace draft. Used
   * by the chat panel when the assistant invoked `save_workflow_package` without an inline
   * package payload. The `snapshotId` is the immutable id the tool returned at preview time;
   * the server loads the snapshot file (NOT the live draft) so a draft mutation between
   * preview and confirm cannot make the apply target a different package than the one shown.
   *
   * sc-395: also threads optional `resolutions` + `acknowledgeDrift` so the chat-side flow
   * (CR-5) can re-resolve a draft-source preview against drift.
   */
  applyPackageImportFromDraft(
    conversationId: string,
    snapshotId: string,
    resolutions?: WorkflowPackageImportResolution[],
    acknowledgeDrift?: boolean,
  ): Observable<WorkflowPackageImportApplyResult> {
    return this.http.post<WorkflowPackageImportApplyResult>(
      '/api/workflows/package/apply-from-draft',
      { conversationId, snapshotId, resolutions, acknowledgeDrift },
    );
  }

  create(payload: WorkflowPayload): Observable<{ key: string; version: number }> {
    return this.http.post<{ key: string; version: number }>('/api/workflows', payload);
  }

  addVersion(key: string, payload: WorkflowPayload): Observable<{ key: string; version: number }> {
    return this.http.put<{ key: string; version: number }>(
      `/api/workflows/${encodeURIComponent(key)}`,
      payload
    );
  }

  retire(key: string): Observable<{ key: string; isRetired: boolean }> {
    return this.http.post<{ key: string; isRetired: boolean }>(
      `/api/workflows/${encodeURIComponent(key)}/retire`,
      {}
    );
  }

  retireMany(keys: string[]): Observable<{ retiredKeys: string[]; missingKeys: string[] }> {
    return this.http.post<{ retiredKeys: string[]; missingKeys: string[] }>(
      '/api/workflows/retire',
      { keys }
    );
  }

  validateScript(request: ValidateScriptRequest): Observable<ValidateScriptResponse> {
    return this.http.post<ValidateScriptResponse>('/api/workflows/validate-script', request);
  }

  /** TN-6: render a Transform node's template against a sample fixture for the inspector
   *  preview pane. Backend validates `template` non-empty and `outputType` ∈ {string,json}; a
   *  Scriban parse/render failure surfaces as 422 with `{ error }`. JSON-parse failure on
   *  `outputType=json` returns 200 with `jsonParseError` set so the UI can show the raw render
   *  alongside the parse-error annotation. */
  renderTransformPreview(request: TransformPreviewRequest): Observable<TransformPreviewResponse> {
    return this.http.post<TransformPreviewResponse>(
      '/api/workflows/templates/render-transform-preview',
      request
    );
  }

  /** F2: per-node dataflow snapshot for the saved workflow at this version. */
  getDataflow(key: string, version: number): Observable<WorkflowDataflowSnapshot> {
    return this.http.get<WorkflowDataflowSnapshot>(
      `/api/workflows/${encodeURIComponent(key)}/${version}/dataflow`
    );
  }

  // ---------- T1: workflow fixtures + dry-run ----------

  listFixtures(workflowKey: string): Observable<WorkflowFixtureSummary[]> {
    return this.http.get<WorkflowFixtureSummary[]>(
      `/api/workflows/${encodeURIComponent(workflowKey)}/fixtures`
    );
  }

  getFixture(workflowKey: string, id: number): Observable<WorkflowFixtureDetail> {
    return this.http.get<WorkflowFixtureDetail>(
      `/api/workflows/${encodeURIComponent(workflowKey)}/fixtures/${id}`
    );
  }

  createFixture(workflowKey: string, payload: WorkflowFixtureCreate): Observable<WorkflowFixtureDetail> {
    return this.http.post<WorkflowFixtureDetail>(
      `/api/workflows/${encodeURIComponent(workflowKey)}/fixtures`,
      payload
    );
  }

  updateFixture(workflowKey: string, id: number, payload: WorkflowFixtureUpdate): Observable<WorkflowFixtureDetail> {
    return this.http.put<WorkflowFixtureDetail>(
      `/api/workflows/${encodeURIComponent(workflowKey)}/fixtures/${id}`,
      payload
    );
  }

  deleteFixture(workflowKey: string, id: number): Observable<void> {
    return this.http.delete<void>(
      `/api/workflows/${encodeURIComponent(workflowKey)}/fixtures/${id}`
    );
  }

  dryRun(workflowKey: string, payload: DryRunRequestBody): Observable<DryRunResponse> {
    return this.http.post<DryRunResponse>(
      `/api/workflows/${encodeURIComponent(workflowKey)}/dry-run`,
      payload
    );
  }
}

/** HAA-14 — Workflow summary annotated with the most recent saga `UpdatedAtUtc`. Returned by
 *  `GET /api/workflows/recent` for the homepage rail. */
export interface RecentWorkflow {
  summary: WorkflowSummary;
  lastUsedAtUtc: string;
}

// ---------- T1 types ----------

export interface WorkflowFixtureSummary {
  id: number;
  workflowKey: string;
  fixtureKey: string;
  displayName: string;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface WorkflowFixtureDetail extends WorkflowFixtureSummary {
  startingInput: string | null;
  /** Map of agentKey → ordered array of mock responses. */
  mockResponses: Record<string, DryRunMockResponse[]>;
}

export interface DryRunMockResponse {
  decision: string;
  output?: string | null;
  payload?: unknown;
}

export interface WorkflowFixtureCreate {
  workflowKey: string;
  fixtureKey: string;
  displayName: string;
  startingInput?: string | null;
  mockResponses?: Record<string, DryRunMockResponse[]>;
}

export interface WorkflowFixtureUpdate {
  fixtureKey: string;
  displayName: string;
  startingInput?: string | null;
  mockResponses?: Record<string, DryRunMockResponse[]>;
}

export interface DryRunRequestBody {
  fixtureId?: number | null;
  workflowVersion?: number | null;
  startingInput?: string | null;
  mockResponses?: Record<string, DryRunMockResponse[]> | null;
}

export type DryRunState = 'Completed' | 'HitlReached' | 'Failed' | 'StepLimitExceeded';

export interface DryRunResponse {
  state: DryRunState;
  terminalPort: string | null;
  failureReason: string | null;
  finalArtifact: string | null;
  hitlPayload: {
    nodeId: string;
    agentKey: string;
    input: string | null;
    /** Legacy `outputTemplate` (singular) on the HITL agent — what the form preview renders. */
    outputTemplate?: string | null;
    /** Per-port templates the saga renders server-side at submit time. */
    decisionOutputTemplates?: Record<string, string> | null;
    /** Best-effort server render of the form preview at suspension; null when no template found. */
    renderedFormPreview?: string | null;
    /** Set when the form-template render failed; the dry-run still succeeds. */
    renderError?: string | null;
  } | null;
  workflowVariables: Record<string, unknown>;
  contextVariables: Record<string, unknown>;
  events: DryRunEvent[];
}

export interface DryRunEvent {
  ordinal: number;
  kind: string;
  nodeId: string;
  nodeKind: string;
  agentKey: string | null;
  portName: string | null;
  message: string | null;
  inputPreview: string | null;
  outputPreview: string | null;
  reviewRound: number | null;
  maxRounds: number | null;
  subflowDepth: number | null;
  subflowKey: string | null;
  subflowVersion: number | null;
  logs: string[] | null;
  decisionPayload: unknown;
}
