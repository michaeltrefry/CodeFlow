import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AgentRoleSkillGrants,
  Skill,
  SkillCreateRequest,
  SkillUpdateRequest,
} from './models';

@Injectable({ providedIn: 'root' })
export class SkillsApi {
  private readonly http = inject(HttpClient);

  list(includeArchived = false): Observable<Skill[]> {
    const params = new HttpParams().set('includeArchived', String(includeArchived));
    return this.http.get<Skill[]>('/api/skills', { params });
  }

  get(id: number): Observable<Skill> {
    return this.http.get<Skill>(`/api/skills/${id}`);
  }

  create(request: SkillCreateRequest): Observable<Skill> {
    return this.http.post<Skill>('/api/skills', request);
  }

  update(id: number, request: SkillUpdateRequest): Observable<Skill> {
    return this.http.put<Skill>(`/api/skills/${id}`, request);
  }

  archive(id: number): Observable<void> {
    return this.http.delete<void>(`/api/skills/${id}`);
  }

  getRoleGrants(roleId: number): Observable<AgentRoleSkillGrants> {
    return this.http.get<AgentRoleSkillGrants>(`/api/agent-roles/${roleId}/skills`);
  }

  replaceRoleGrants(roleId: number, skillIds: number[]): Observable<AgentRoleSkillGrants> {
    return this.http.put<AgentRoleSkillGrants>(
      `/api/agent-roles/${roleId}/skills`,
      { skillIds }
    );
  }
}
