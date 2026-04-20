import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AgentRole,
  AgentRoleCreateRequest,
  AgentRoleGrant,
  AgentRoleUpdateRequest,
} from './models';

@Injectable({ providedIn: 'root' })
export class AgentRolesApi {
  private readonly http = inject(HttpClient);

  list(includeArchived = false): Observable<AgentRole[]> {
    const params = new HttpParams().set('includeArchived', String(includeArchived));
    return this.http.get<AgentRole[]>('/api/agent-roles', { params });
  }

  get(id: number): Observable<AgentRole> {
    return this.http.get<AgentRole>(`/api/agent-roles/${id}`);
  }

  create(request: AgentRoleCreateRequest): Observable<AgentRole> {
    return this.http.post<AgentRole>('/api/agent-roles', request);
  }

  update(id: number, request: AgentRoleUpdateRequest): Observable<AgentRole> {
    return this.http.put<AgentRole>(`/api/agent-roles/${id}`, request);
  }

  archive(id: number): Observable<void> {
    return this.http.delete<void>(`/api/agent-roles/${id}`);
  }

  getGrants(id: number): Observable<AgentRoleGrant[]> {
    return this.http.get<AgentRoleGrant[]>(`/api/agent-roles/${id}/tools`);
  }

  replaceGrants(id: number, grants: AgentRoleGrant[]): Observable<AgentRoleGrant[]> {
    return this.http.put<AgentRoleGrant[]>(`/api/agent-roles/${id}/tools`, grants);
  }

  getRolesForAgent(agentKey: string): Observable<AgentRole[]> {
    return this.http.get<AgentRole[]>(`/api/agents/${encodeURIComponent(agentKey)}/roles`);
  }

  replaceAssignments(agentKey: string, roleIds: number[]): Observable<AgentRole[]> {
    return this.http.put<AgentRole[]>(
      `/api/agents/${encodeURIComponent(agentKey)}/roles`,
      { roleIds }
    );
  }
}
