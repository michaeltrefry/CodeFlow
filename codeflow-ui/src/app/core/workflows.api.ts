import { HttpClient } from '@angular/common/http';
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

export interface ValidateScriptRequest {
  script: string;
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

  listVersions(key: string): Observable<WorkflowDetail[]> {
    return this.http.get<WorkflowDetail[]>(`/api/workflows/${encodeURIComponent(key)}/versions`);
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
}
