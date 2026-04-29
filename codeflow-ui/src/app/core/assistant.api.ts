import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { TokenUsageRollup } from './models';

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

/**
 * HAA-14 — Slim conversation summary used by the homepage rail's resume-conversation list.
 * `messageCount` lets the rail distinguish never-used homepage threads (count = 0) from
 * threads with content; `firstUserMessagePreview` is a server-truncated label, null when no
 * user message has been sent yet.
 */
export interface AssistantConversationSummary {
  id: string;
  scope: AssistantScope;
  syntheticTraceId: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  messageCount: number;
  firstUserMessagePreview: string | null;
}

export interface ListConversationsResponse {
  conversations: AssistantConversationSummary[];
}

/**
 * HAA-14 — Aggregated assistant token usage for the current user. Drives the rail's
 * assistant-token chip. `today` is calendar UTC; `perConversation` covers each thread that has
 * captured at least one record (empty threads filter out so the rail doesn't render zero rows).
 */
export interface AssistantConversationTokenUsage {
  conversationId: string;
  syntheticTraceId: string;
  scope: AssistantScope;
  rollup: TokenUsageRollup;
}

export interface AssistantTokenUsageSummary {
  today: TokenUsageRollup;
  allTime: TokenUsageRollup;
  perConversation: AssistantConversationTokenUsage[];
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

  /**
   * HAA-14 — Lists the caller's recent conversations for the homepage resume-list rail.
   * Anonymous demo-mode callers see only the conversations attached to their `cf_anon_id`
   * cookie (typically just the homepage thread).
   */
  listConversations(limit?: number): Observable<ListConversationsResponse> {
    const params = limit ? { limit: String(limit) } : undefined;
    return this.http.get<ListConversationsResponse>('/api/assistant/conversations', { params });
  }

  /**
   * HAA-14 — Aggregated assistant token usage for the rail chip. Sums across every synthetic
   * trace the caller owns; `today` cuts at calendar UTC midnight.
   */
  getTokenUsageSummary(): Observable<AssistantTokenUsageSummary> {
    return this.http.get<AssistantTokenUsageSummary>('/api/assistant/token-usage/summary');
  }
}
