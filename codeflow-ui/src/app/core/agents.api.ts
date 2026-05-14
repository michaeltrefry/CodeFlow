import { HttpClient, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AgentConfig,
  AgentResolvedTools,
  AgentSummary,
  AgentVersion,
  AgentVersionSummary,
  DecisionOutputTemplatePreviewRequest,
  DecisionOutputTemplatePreviewResponse,
  PromptTemplatePreviewRequest,
  PromptTemplatePreviewResponse
} from './models';
import {
  WorkflowPackageImportApplyResult,
  WorkflowPackageImportPreview,
  WorkflowPackageImportResolution,
} from './workflows.api';

@Injectable({ providedIn: 'root' })
export class AgentsApi {
  private readonly http = inject(HttpClient);

  list(tags?: string[]): Observable<AgentSummary[]> {
    if (!tags || tags.length === 0) {
      return this.http.get<AgentSummary[]>('/api/agents');
    }

    return this.http.get<AgentSummary[]>('/api/agents', {
      params: { tag: tags }
    });
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

  /** Epic 993 / NO-8: the host/MCP tool identifiers an agent resolves through its role grants
   *  at a given version. Feeds the workflow editor's node-overrides tools picker. */
  getResolvedTools(key: string, version: number): Observable<AgentResolvedTools> {
    return this.http.get<AgentResolvedTools>(
      `/api/agents/${encodeURIComponent(key)}/${version}/resolved-tools`
    );
  }

  create(key: string, config: AgentConfig, tags?: string[]): Observable<{ key: string; version: number }> {
    return this.http.post<{ key: string; version: number }>(
      '/api/agents',
      tags === undefined ? { key, config } : { key, config, tags }
    );
  }

  addVersion(key: string, config: AgentConfig, tags?: string[]): Observable<{ key: string; version: number }> {
    return this.http.put<{ key: string; version: number }>(
      `/api/agents/${encodeURIComponent(key)}`,
      tags === undefined ? { config } : { config, tags }
    );
  }

  /** AP-6 (sc-837): download the canonical agent-package JSON for (key, version). The
   *  endpoint streams `application/json` with a Content-Disposition attachment header that
   *  the caller uses for the saved filename. Routes through HttpClient (not an `<a download>`)
   *  so the auth interceptor attaches the bearer token — anchor downloads bypass it and 401. */
  downloadPackage(key: string, version: number): Observable<HttpResponse<Blob>> {
    return this.http.get(`/api/agents/${encodeURIComponent(key)}/${version}/package`, {
      observe: 'response',
      responseType: 'blob'
    });
  }

  /** AP-7 (sc-838): preview an agent-package import. Mirrors `WorkflowsApi.previewPackageImport`
   *  exactly — same request body (`{ package, resolutions }`) and response shape
   *  (`WorkflowPackageImportPreview`) — so the imports page can dispatch on `schemaVersion`
   *  without re-modeling the per-row preview surface. */
  previewPackageImport(
    agentPackage: unknown,
    resolutions?: WorkflowPackageImportResolution[],
  ): Observable<WorkflowPackageImportPreview> {
    return this.http.post<WorkflowPackageImportPreview>(
      '/api/agents/package/preview',
      { package: agentPackage, resolutions },
    );
  }

  /** AP-7 (sc-838): apply an agent-package import. Same drift-ack contract as the workflow
   *  apply: a 409 `WorkflowPackageImportDriftConflict` retries with `acknowledgeDrift: true`. */
  applyPackageImport(
    agentPackage: unknown,
    resolutions?: WorkflowPackageImportResolution[],
    acknowledgeDrift?: boolean,
  ): Observable<WorkflowPackageImportApplyResult> {
    return this.http.post<WorkflowPackageImportApplyResult>(
      '/api/agents/package/apply',
      { package: agentPackage, resolutions, acknowledgeDrift },
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
