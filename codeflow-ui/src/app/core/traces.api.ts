import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  CreateTraceRequest,
  CreateTraceResponse,
  HitlDecisionRequest,
  HitlTask,
  TraceDetail,
  TraceSummary
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

  create(request: CreateTraceRequest): Observable<CreateTraceResponse> {
    return this.http.post<CreateTraceResponse>('/api/traces', request);
  }

  pendingHitl(): Observable<HitlTask[]> {
    return this.http.get<HitlTask[]>('/api/traces/hitl/pending');
  }

  submitHitlDecision(traceId: string, request: HitlDecisionRequest): Observable<{ taskId: number }> {
    return this.http.post<{ taskId: number }>(`/api/traces/${traceId}/hitl-decision`, request);
  }
}
