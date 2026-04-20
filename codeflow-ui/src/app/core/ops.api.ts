import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export interface DeadLetterQueue {
  queueName: string;
  messageCount: number;
}

export interface DeadLetterMessage {
  messageId: string;
  queueName: string;
  originalInputAddress: string | null;
  faultExceptionMessage: string | null;
  faultExceptionType: string | null;
  firstFaultedAtUtc: string | null;
  payloadPreview: string;
}

export interface DeadLetterListResponse {
  queues: DeadLetterQueue[];
  messages: DeadLetterMessage[];
}

export interface DeadLetterRetryResponse {
  success: boolean;
  republishedTo: string | null;
  errorMessage: string | null;
}

@Injectable({ providedIn: 'root' })
export class OpsApi {
  private readonly http = inject(HttpClient);

  listDlq(): Observable<DeadLetterListResponse> {
    return this.http.get<DeadLetterListResponse>('/api/ops/dlq');
  }

  retry(queueName: string, messageId: string): Observable<DeadLetterRetryResponse> {
    return this.http.post<DeadLetterRetryResponse>(
      `/api/ops/dlq/${encodeURIComponent(queueName)}/retry/${encodeURIComponent(messageId)}`,
      {});
  }

  metrics(): Observable<string> {
    return this.http.get('/api/ops/metrics', { responseType: 'text' });
  }
}
