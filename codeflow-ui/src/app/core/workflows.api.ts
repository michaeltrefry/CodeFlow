import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { WorkflowDetail, WorkflowEdge, WorkflowSummary } from './models';

export interface WorkflowPayload {
  key?: string;
  name: string;
  startAgentKey: string;
  escalationAgentKey?: string | null;
  maxRoundsPerRound: number;
  edges: WorkflowEdge[];
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
}
