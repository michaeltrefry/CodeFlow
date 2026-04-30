import { HttpClient, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  BulkDeleteTracesRequest,
  BulkDeleteTracesResponse,
  CreateTraceRequest,
  CreateTraceResponse,
  HitlDecisionRequest,
  HitlTask,
  ReplayRequest,
  ReplayResponse,
  TraceDescendant,
  TraceDetail,
  TraceSummary,
  TraceTokenUsageDto
} from './models';

@Injectable({ providedIn: 'root' })
export class TracesApi {
  private readonly http = inject(HttpClient);

  list(): Observable<TraceSummary[]> {
    return this.http.get<TraceSummary[]>('/api/traces');
  }

  get(id: string): Observable<TraceDetail> {
    return this.http.get<TraceDetail>(`/api/traces/${id}`);
  }

  getDescendants(id: string): Observable<TraceDescendant[]> {
    return this.http.get<TraceDescendant[]>(`/api/traces/${id}/descendants`);
  }

  create(request: CreateTraceRequest): Observable<CreateTraceResponse> {
    return this.http.post<CreateTraceResponse>('/api/traces', request);
  }

  pendingHitl(): Observable<HitlTask[]> {
    return this.http.get<HitlTask[]>('/api/traces/hitl/pending');
  }

  submitHitlDecision(traceId: string, request: HitlDecisionRequest): Observable<{ taskId: number }> {
    return this.http.post<{ taskId: number }>(`/api/traces/${traceId}/hitl-decision`, request);
  }

  terminate(traceId: string): Observable<void> {
    return this.http.post<void>(`/api/traces/${traceId}/terminate`, {});
  }

  delete(traceId: string): Observable<void> {
    return this.http.delete<void>(`/api/traces/${traceId}`);
  }

  bulkDelete(request: BulkDeleteTracesRequest): Observable<BulkDeleteTracesResponse> {
    return this.http.post<BulkDeleteTracesResponse>('/api/traces/bulk-delete', request);
  }

  getArtifact(traceId: string, artifactUri: string): Observable<string> {
    return this.http.get(`/api/traces/${traceId}/artifact`, {
      params: { uri: artifactUri },
      responseType: 'text'
    });
  }

  downloadArtifact(traceId: string, artifactUri: string): Observable<HttpResponse<Blob>> {
    return this.http.get(`/api/traces/${traceId}/artifact`, {
      params: { uri: artifactUri },
      observe: 'response',
      responseType: 'blob'
    });
  }

  replay(traceId: string, request: ReplayRequest): Observable<ReplayResponse> {
    return this.http.post<ReplayResponse>(`/api/traces/${traceId}/replay`, request);
  }

  /** Token Usage Tracking [Slice 5/6]: per-trace rollup at every level
   *  (per-call, per-invocation, per-node, per-scope, per-trace) with
   *  provider+model breakdowns. The inspector calls this once on open
   *  and then merges incoming `TokenUsageRecorded` SSE events into the
   *  rollups in-memory. */
  getTokenUsage(traceId: string): Observable<TraceTokenUsageDto> {
    return this.http.get<TraceTokenUsageDto>(`/api/traces/${traceId}/token-usage`);
  }
}
