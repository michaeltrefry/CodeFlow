import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  McpServer,
  McpServerCreateRequest,
  McpServerRefreshResponse,
  McpServerTool,
  McpServerUpdateRequest,
  McpServerVerifyResponse,
} from './models';

@Injectable({ providedIn: 'root' })
export class McpServersApi {
  private readonly http = inject(HttpClient);

  list(includeArchived = false): Observable<McpServer[]> {
    const params = new HttpParams().set('includeArchived', String(includeArchived));
    return this.http.get<McpServer[]>('/api/mcp-servers', { params });
  }

  get(id: number): Observable<McpServer> {
    return this.http.get<McpServer>(`/api/mcp-servers/${id}`);
  }

  create(request: McpServerCreateRequest): Observable<McpServer> {
    return this.http.post<McpServer>('/api/mcp-servers', request);
  }

  update(id: number, request: McpServerUpdateRequest): Observable<McpServer> {
    return this.http.put<McpServer>(`/api/mcp-servers/${id}`, request);
  }

  archive(id: number): Observable<void> {
    return this.http.delete<void>(`/api/mcp-servers/${id}`);
  }

  verify(id: number): Observable<McpServerVerifyResponse> {
    return this.http.post<McpServerVerifyResponse>(`/api/mcp-servers/${id}/verify`, {});
  }

  refreshTools(id: number): Observable<McpServerRefreshResponse> {
    return this.http.post<McpServerRefreshResponse>(`/api/mcp-servers/${id}/refresh-tools`, {});
  }

  getTools(id: number): Observable<McpServerTool[]> {
    return this.http.get<McpServerTool[]>(`/api/mcp-servers/${id}/tools`);
  }
}
