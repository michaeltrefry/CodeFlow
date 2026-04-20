import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { AgentConfig, AgentSummary, AgentVersion, AgentVersionSummary } from './models';

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
}
