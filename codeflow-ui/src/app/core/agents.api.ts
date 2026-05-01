import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AgentConfig,
  AgentSummary,
  AgentVersion,
  AgentVersionSummary,
  DecisionOutputTemplatePreviewRequest,
  DecisionOutputTemplatePreviewResponse,
  PromptTemplatePreviewRequest,
  PromptTemplatePreviewResponse
} from './models';

@Injectable({ providedIn: 'root' })
export class AgentsApi {
  private readonly http = inject(HttpClient);

  list(): Observable<AgentSummary[]> {
    return this.http.get<AgentSummary[]>('/api/agents');
  }

  versions(key: string): Observable<AgentVersionSummary[]> {
    return this.http.get<AgentVersionSummary[]>(`/api/agents/${encodeURIComponent(key)}/versions`);
  }

  getVersion(key: string, version: number): Observable<AgentVersion> {
    return this.http.get<AgentVersion>(`/api/agents/${encodeURIComponent(key)}/${version}`);
  }

  getLatest(key: string): Observable<AgentVersion> {
    return this.http.get<AgentVersion>(`/api/agents/${encodeURIComponent(key)}`);
  }

  create(key: string, config: AgentConfig): Observable<{ key: string; version: number }> {
    return this.http.post<{ key: string; version: number }>('/api/agents', { key, config });
  }

  addVersion(key: string, config: AgentConfig): Observable<{ key: string; version: number }> {
    return this.http.put<{ key: string; version: number }>(
      `/api/agents/${encodeURIComponent(key)}`,
      { config }
    );
  }

  retire(key: string): Observable<{ key: string; isRetired: boolean }> {
    return this.http.post<{ key: string; isRetired: boolean }>(
      `/api/agents/${encodeURIComponent(key)}/retire`,
      {}
    );
  }

  retireMany(keys: string[]): Observable<{ retiredKeys: string[]; missingKeys: string[] }> {
    return this.http.post<{ retiredKeys: string[]; missingKeys: string[] }>(
      '/api/agents/retire',
      { keys }
    );
  }

  fork(request: {
    sourceKey: string;
    sourceVersion: number;
    workflowKey: string;
    config: AgentConfig;
  }): Observable<{
    key: string;
    version: number;
    forkedFromKey: string;
    forkedFromVersion: number;
    owningWorkflowKey: string;
  }> {
    return this.http.post<{
      key: string;
      version: number;
      forkedFromKey: string;
      forkedFromVersion: number;
      owningWorkflowKey: string;
    }>('/api/agents/fork', request);
  }

  getPublishStatus(forkKey: string): Observable<{
    forkedFromKey: string;
    forkedFromVersion: number;
    originalLatestVersion: number | null;
    isDrift: boolean;
  }> {
    return this.http.get<{
      forkedFromKey: string;
      forkedFromVersion: number;
      originalLatestVersion: number | null;
      isDrift: boolean;
    }>(`/api/agents/${encodeURIComponent(forkKey)}/publish-status`);
  }

  publish(forkKey: string, request: {
    mode: 'original' | 'new-agent';
    newKey?: string;
    acknowledgeDrift?: boolean;
  }): Observable<{
    publishedKey: string;
    publishedVersion: number;
    forkedFromKey: string;
    forkedFromVersion: number;
  }> {
    return this.http.post<{
      publishedKey: string;
      publishedVersion: number;
      forkedFromKey: string;
      forkedFromVersion: number;
    }>(`/api/agents/${encodeURIComponent(forkKey)}/publish`, request);
  }

  renderDecisionOutputTemplate(
    request: DecisionOutputTemplatePreviewRequest
  ): Observable<DecisionOutputTemplatePreviewResponse> {
    return this.http.post<DecisionOutputTemplatePreviewResponse>(
      '/api/agents/templates/render-preview',
      request
    );
  }

  renderPromptTemplate(
    request: PromptTemplatePreviewRequest
  ): Observable<PromptTemplatePreviewResponse> {
    return this.http.post<PromptTemplatePreviewResponse>(
      '/api/agents/templates/render-prompt-preview',
      request
    );
  }
}
