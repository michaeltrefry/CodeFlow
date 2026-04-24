import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  LlmProviderKey,
  LlmProviderModelOption,
  LlmProviderResponse,
  LlmProviderWriteRequest,
} from './models';

@Injectable({ providedIn: 'root' })
export class LlmProvidersApi {
  private readonly http = inject(HttpClient);

  list(): Observable<LlmProviderResponse[]> {
    return this.http.get<LlmProviderResponse[]>('/api/admin/llm-providers');
  }

  set(provider: LlmProviderKey, request: LlmProviderWriteRequest): Observable<LlmProviderResponse> {
    return this.http.put<LlmProviderResponse>(`/api/admin/llm-providers/${provider}`, request);
  }

  listModels(): Observable<LlmProviderModelOption[]> {
    return this.http.get<LlmProviderModelOption[]>('/api/llm-providers/models');
  }
}
