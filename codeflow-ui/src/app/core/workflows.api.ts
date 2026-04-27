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

export type WorkflowPackageImportAction = 'Create' | 'Reuse' | 'Conflict';
export type WorkflowPackageImportResourceKind =
  | 'Workflow'
  | 'Agent'
  | 'AgentRoleAssignment'
  | 'Role'
  | 'Skill'
  | 'McpServer';

export interface WorkflowPackageReference {
  key: string;
  version: number;
}

export interface WorkflowPackageImportItem {
  kind: WorkflowPackageImportResourceKind;
  key: string;
  version?: number | null;
  action: WorkflowPackageImportAction;
  message: string;
}

export interface WorkflowPackageImportPreview {
  entryPoint: WorkflowPackageReference;
  items: WorkflowPackageImportItem[];
  warnings: string[];
  createCount: number;
  reuseCount: number;
  conflictCount: number;
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

@Injectable({ providedIn: 'root' })
export class WorkflowsApi {
  private readonly http = inject(HttpClient);

  list(): Observable<WorkflowSummary[]> {
    return this.http.get<WorkflowSummary[]>('/api/workflows');
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

  previewPackageImport(workflowPackage: unknown): Observable<WorkflowPackageImportPreview> {
    return this.http.post<WorkflowPackageImportPreview>('/api/workflows/package/preview', workflowPackage);
  }

  applyPackageImport(workflowPackage: unknown): Observable<WorkflowPackageImportApplyResult> {
    return this.http.post<WorkflowPackageImportApplyResult>('/api/workflows/package/apply', workflowPackage);
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

  validateScript(request: ValidateScriptRequest): Observable<ValidateScriptResponse> {
    return this.http.post<ValidateScriptResponse>('/api/workflows/validate-script', request);
  }

  /** F2: per-node dataflow snapshot for the saved workflow at this version. */
  getDataflow(key: string, version: number): Observable<WorkflowDataflowSnapshot> {
    return this.http.get<WorkflowDataflowSnapshot>(
      `/api/workflows/${encodeURIComponent(key)}/${version}/dataflow`
    );
  }
}
