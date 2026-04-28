import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

export type AssistantScopeKind = 'homepage' | 'entity';

export interface AssistantScope {
  kind: AssistantScopeKind;
  entityType?: string;
  entityId?: string;
}

export type AssistantMessageRole = 'system' | 'user' | 'assistant';

export interface AssistantMessage {
  id: string;
  sequence: number;
  role: AssistantMessageRole;
  content: string;
  provider: string | null;
  model: string | null;
  invocationId: string | null;
  createdAtUtc: string;
}

export interface AssistantConversation {
  id: string;
  scope: AssistantScope;
  syntheticTraceId: string;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface ConversationResponse {
  conversation: AssistantConversation;
  messages: AssistantMessage[];
}

@Injectable({ providedIn: 'root' })
export class AssistantApi {
  private readonly http = inject(HttpClient);

  /**
   * Get-or-create the conversation for `(currentUser, scope)`. Returns the conversation plus
   * any prior messages so the chat panel can hydrate immediately.
   */
  getOrCreate(scope: AssistantScope): Observable<ConversationResponse> {
    return this.http.post<ConversationResponse>('/api/assistant/conversations', { scope });
  }

  get(conversationId: string): Observable<ConversationResponse> {
    return this.http.get<ConversationResponse>(
      `/api/assistant/conversations/${encodeURIComponent(conversationId)}`,
    );
  }
}
