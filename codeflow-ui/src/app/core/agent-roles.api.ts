import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AgentRole,
  AgentRoleCreateRequest,
  AgentRoleGrant,
  AgentRoleUpdateRequest,
} from './models';

/**
 * sc-828 / AR-4: response from PUT /api/agents/{key}/roles. Bump-on-write produces a new
 * agent version with the supplied assignment slot; the response surfaces the new version
 * so the admin UI can redirect/refresh and so workflows can rebind by republishing.
 */
export interface AgentAssignmentsResponse {
  agentKey: string;
  agentVersion: number;
  assignedRoles: AgentRole[];
}

/**
 * sc-828 / AR-4: 409 body returned when the bump previewed against `expectedFromVersion`
 * but the agent's latest moved on to `actualLatestVersion`. The UI catches this, refreshes
 * its view of the agent, and offers to retry with `acknowledgeDrift: true`.
 */
export interface AgentAssignmentsDriftResponse {
  agentKey: string;
  expectedFromVersion: number;
  actualLatestVersion: number;
  message: string;
}

export interface ReplaceAssignmentsOptions {
  expectedFromVersion?: number;
  acknowledgeDrift?: boolean;
}

@Injectable({ providedIn: 'root' })
export class AgentRolesApi {
  private readonly http = inject(HttpClient);

  list(includeArchived = false, tags?: string[]): Observable<AgentRole[]> {
    let params = new HttpParams().set('includeArchived', String(includeArchived));
    for (const tag of tags ?? []) {
      params = params.append('tag', tag);
    }

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

  retire(id: number): Observable<AgentRole> {
    return this.http.post<AgentRole>(`/api/agent-roles/${id}/retire`, {});
  }

  retireMany(ids: number[]): Observable<{ retiredIds: number[]; missingIds: number[] }> {
    return this.http.post<{ retiredIds: number[]; missingIds: number[] }>(
      '/api/agent-roles/retire',
      { ids }
    );
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

  replaceAssignments(
    agentKey: string,
    roleIds: number[],
    options: ReplaceAssignmentsOptions = {}
  ): Observable<AgentAssignmentsResponse> {
    return this.http.put<AgentAssignmentsResponse>(
      `/api/agents/${encodeURIComponent(agentKey)}/roles`,
      {
        roleIds,
        expectedFromVersion: options.expectedFromVersion,
        acknowledgeDrift: options.acknowledgeDrift ?? false,
      }
    );
  }
}
