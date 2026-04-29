import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AssistantSettingsResponse,
  AssistantSettingsWriteRequest,
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

  /** HAA-15 — DB-backed admin defaults for the homepage AI assistant. */
  getAssistantSettings(): Observable<AssistantSettingsResponse> {
    return this.http.get<AssistantSettingsResponse>('/api/admin/assistant-settings');
  }

  setAssistantSettings(request: AssistantSettingsWriteRequest): Observable<AssistantSettingsResponse> {
    return this.http.put<AssistantSettingsResponse>('/api/admin/assistant-settings', request);
  }
}
