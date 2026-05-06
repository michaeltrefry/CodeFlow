import type { Observable } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { AssistantMessage } from './assistant.api';
import type { PageContextDto } from './page-context';
import type { SseFrame } from './sse-stream';
import { streamSse } from './sse-stream';

/**
 * One frame from the assistant SSE stream. Mirrors the event names emitted by
 * <c>AssistantEndpoints.WriteEventAsync</c> in the API:
 *   - <c>user-message-persisted</c>: full user-turn message row, with stable id + sequence.
 *   - <c>text-delta</c>: incremental assistant content; concatenate to build the running text.
 *   - <c>token-usage</c>: provider-reported usage for the just-completed turn.
 *   - <c>tool-call</c>: assistant requested a tool invocation. Render in pending state.
 *   - <c>tool-result</c>: tool finished. Pair with the matching tool-call by `id`.
 *   - <c>assistant-message-persisted</c>: full assistant-turn message row, with finalized
 *     content + provider/model/invocationId.
 *   - <c>done</c>: terminal — stream is closing.
 *   - <c>error</c>: terminal failure with a free-form message.
 *   - <c>preflight-refused</c>: sc-274 phase 2 — server short-circuited before the SSE
 *     stream opened because ambiguity preflight refused the prompt. Carries the parsed
 *     422 body so the UI can render clarification questions inline.
 */
export type AssistantStreamEvent =
  | { kind: 'user-message-persisted'; message: AssistantMessage }
  | { kind: 'text-delta'; delta: string }
  | {
      kind: 'token-usage';
      recordId: string;
      provider: string;
      model: string;
      usage: unknown;
      /** HAA-17 — cumulative input tokens captured against this conversation after the turn. */
      conversationInputTokensTotal: number;
      /** HAA-17 — cumulative output tokens; mirrors `conversationInputTokensTotal`. */
      conversationOutputTokensTotal: number;
    }
  | { kind: 'tool-call'; id: string; name: string; arguments: unknown }
  | { kind: 'tool-result'; id: string; name: string; result: string; isError: boolean }
  | { kind: 'assistant-message-persisted'; message: AssistantMessage }
  | { kind: 'artifact-event'; event: ArtifactEvent; supersedesPriorByName: boolean }
  | { kind: 'done' }
  /**
   * sc-806 — `code` is a stable string from `AssistantTurnErrorCodes` on the server. The
   * chat panel branches on it: `turn-still-running` surfaces a Cancel affordance alongside
   * Retry because retrying re-enters the same wait. Older servers that haven't deployed
   * AR-4 yet may emit error frames without a code; consumers must treat null as "generic
   * error, Retry only".
   */
  | { kind: 'error'; code: string | null; message: string }
  | { kind: 'preflight-refused'; payload: AssistantPreflightRefusal };

/**
 * sc-793 (AA-2): one persisted artifact-event row, projected to the SSE payload shape. The
 * full event model lives server-side in `assistant_artifact_events`; the chat panel only
 * needs the fields below to render a pill, anchor it by `id`, and (in AA-3) re-hydrate from
 * conversation load. AA-4 wires the Download / View actions against the durable `id`.
 */
export interface ArtifactEvent {
  /** Stable per-event id; the AA-4 download endpoint keys off this. */
  id: string;
  /** The conversation that owns this event. */
  conversationId: string;
  /** Monotonic per-conversation sequence — drives inline-pill ordering. */
  sequence: number;
  /** Server enum name (e.g. 'WorkflowPackageDraft'). */
  kind: string;
  /** Display name (e.g. 'draft.cf-workflow-package.json'). */
  name: string;
  /** Non-null only for immutable per-save snapshots. */
  snapshotId: string | null;
  /** Tool-supplied summary, JSON-stringified. Free-form; renderer is resilient to missing keys. */
  summary: string | null;
  /** ISO timestamp the row was created. */
  createdAtUtc: string;
}

/**
 * sc-274 phase 2 — parsed body of a preflight 422 from the assistant chat endpoint. Mirrors
 * <c>AssistantPreflightRefusalResponse</c> on the server. Drives the chat panel's preflight
 * banner: clarification questions, lowest-dimension reason, and the score / threshold
 * metric.
 */
export interface AssistantPreflightRefusal {
  conversationId: string;
  code: 'assistant-preflight-ambiguous';
  mode: string;
  overallScore: number;
  threshold: number;
  dimensions: Array<{ dimension: string; score: number; reason: string | null }>;
  missingFields: string[];
  clarificationQuestions: string[];
}

/**
 * Streams a single chat turn against <c>POST /api/assistant/conversations/{id}/messages</c>.
 * Uses the shared fetch-based SSE helper so the Authorization header can be attached
 * (EventSource cannot set custom headers and bypasses the auth interceptor).
 */
export interface AssistantWorkspaceTargetDto {
  /** 'Conversation' (default per-chat dir) or 'Trace' (the workdir of an existing trace). */
  kind: 'Conversation' | 'Trace';
  /** Required for Trace; the trace's GUID (any standard string form, the server normalizes). */
  traceId?: string;
}

export interface SendTurnOptions {
  pageContext?: PageContextDto;
  /** HAA-16 — per-turn provider override (lower-cased canonical key). Falls back to admin default. */
  provider?: string;
  /** HAA-16 — per-turn model override. Falls back to admin default's model for the provider. */
  model?: string;
  /**
   * HAA-19 — per-turn workspace selection. Falls back to the conversation's own workspace when
   * absent. Use Trace to point the assistant's host tools at a code-aware workflow's workdir.
   */
  workspaceOverride?: AssistantWorkspaceTargetDto;
  /**
   * sc-525 — Opaque per-turn idempotency key (UUID-shaped). When supplied, sent as the
   * `Idempotency-Key` header so server-side dedupe can replay the recorded SSE events on
   * retried POSTs without persisting a second user message or invoking the LLM twice. Also
   * gates auto-retry in <c>streamSse</c> on transient network failures: callers that don't
   * pass a key keep the current "fail loud" behavior so they can't accidentally duplicate.
   */
  idempotencyKey?: string;
}

/**
 * sc-809 (AR-7) — Best-effort cancel for an in-flight assistant turn. The chat panel calls
 * this when the user clicks Cancel; after AR-6 the turn runs in a background task on the
 * server that survives client disconnect, so the only way to actually take it down is this
 * DELETE. Fire-and-forget: a network failure is fine, the lifetime ceiling will reap a
 * stuck task eventually.
 */
export async function cancelLiveTurn(
  conversationId: string,
  idempotencyKey: string,
  auth: AuthService,
): Promise<void> {
  const accessToken = auth.getValidAccessToken();
  const headers: Record<string, string> = {};
  if (accessToken) {
    headers['Authorization'] = `Bearer ${accessToken}`;
  }
  try {
    await fetch(
      `/api/assistant/conversations/${encodeURIComponent(conversationId)}/turns/${encodeURIComponent(idempotencyKey)}`,
      { method: 'DELETE', headers },
    );
  } catch {
    // Best-effort — never block the UI on a cancel network failure.
  }
}

export function streamAssistantTurn(
  conversationId: string,
  content: string,
  auth: AuthService,
  options?: SendTurnOptions,
): Observable<AssistantStreamEvent> {
  const body: {
    content: string;
    pageContext?: PageContextDto;
    provider?: string;
    model?: string;
    workspaceOverride?: AssistantWorkspaceTargetDto;
  } = { content };
  if (options?.pageContext) {
    body.pageContext = options.pageContext;
  }
  if (options?.provider) {
    body.provider = options.provider;
  }
  if (options?.model) {
    body.model = options.model;
  }
  if (options?.workspaceOverride) {
    body.workspaceOverride = options.workspaceOverride;
  }

  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (options?.idempotencyKey) {
    headers['Idempotency-Key'] = options.idempotencyKey;
  }

  return streamSse(
    `/api/assistant/conversations/${encodeURIComponent(conversationId)}/messages`,
    {
      method: 'POST',
      headers,
      body: JSON.stringify(body),
      accessToken: auth.getValidAccessToken(),
      handleErrorResponse: handlePreflightRefusal,
      // sc-525 — only retry transient network failures when the caller has set an
      // idempotency key. Without one, a silent retry could create duplicate user messages
      // or duplicate billable LLM turns; with one, the server replays recorded events.
      retry: options?.idempotencyKey ? { attempts: 3 } : undefined,
    },
    parseSseFrame,
  );
}

/**
 * sc-274 phase 2 — turns a 422 with a preflight body into a synthetic stream event so the
 * normal stream-event handler can render the clarification banner. Other error statuses
 * (and 422s without the preflight code) fall through to the default error path.
 */
async function handlePreflightRefusal(response: Response): Promise<AssistantStreamEvent | null> {
  if (response.status !== 422) {
    return null;
  }
  let body: unknown;
  try {
    body = await response.json();
  } catch {
    return null;
  }
  if (!body || typeof body !== 'object') {
    return null;
  }
  const candidate = body as Partial<AssistantPreflightRefusal>;
  if (candidate.code !== 'assistant-preflight-ambiguous') {
    return null;
  }
  return {
    kind: 'preflight-refused',
    payload: {
      conversationId: candidate.conversationId ?? '',
      code: 'assistant-preflight-ambiguous',
      mode: candidate.mode ?? 'AssistantChat',
      overallScore: candidate.overallScore ?? 0,
      threshold: candidate.threshold ?? 0,
      dimensions: Array.isArray(candidate.dimensions) ? candidate.dimensions : [],
      missingFields: Array.isArray(candidate.missingFields) ? candidate.missingFields : [],
      clarificationQuestions: Array.isArray(candidate.clarificationQuestions)
        ? candidate.clarificationQuestions
        : [],
    },
  };
}

export function parseSseFrame({ eventName, dataLines }: SseFrame): AssistantStreamEvent | null {
  if (!eventName) {
    return null;
  }

  const dataJson = dataLines.join('\n');
  let payload: unknown = {};
  if (dataJson.length > 0) {
    try {
      payload = JSON.parse(dataJson);
    } catch {
      return null;
    }
  }

  switch (eventName) {
    case 'user-message-persisted':
      return { kind: 'user-message-persisted', message: payload as AssistantMessage };
    case 'text-delta':
      return { kind: 'text-delta', delta: (payload as { delta: string }).delta ?? '' };
    case 'token-usage': {
      const u = payload as {
        recordId: string;
        provider: string;
        model: string;
        usage: unknown;
        conversationInputTokensTotal?: number;
        conversationOutputTokensTotal?: number;
      };
      return {
        kind: 'token-usage',
        recordId: u.recordId,
        provider: u.provider,
        model: u.model,
        usage: u.usage,
        conversationInputTokensTotal: u.conversationInputTokensTotal ?? 0,
        conversationOutputTokensTotal: u.conversationOutputTokensTotal ?? 0,
      };
    }
    case 'assistant-message-persisted':
      return { kind: 'assistant-message-persisted', message: payload as AssistantMessage };
    case 'tool-call': {
      const c = payload as { id: string; name: string; arguments: unknown };
      return { kind: 'tool-call', id: c.id, name: c.name, arguments: c.arguments };
    }
    case 'tool-result': {
      const r = payload as { id: string; name: string; result: string; isError: boolean };
      return { kind: 'tool-result', id: r.id, name: r.name, result: r.result, isError: r.isError ?? false };
    }
    case 'artifact-event': {
      const a = payload as {
        id: string;
        conversationId: string;
        sequence: number;
        kind: string;
        name: string;
        snapshotId: string | null;
        summary: string | null;
        createdAtUtc: string;
        supersedesPriorByName: boolean;
      };
      return {
        kind: 'artifact-event',
        event: {
          id: a.id,
          conversationId: a.conversationId,
          sequence: a.sequence,
          kind: a.kind,
          name: a.name,
          snapshotId: a.snapshotId ?? null,
          summary: a.summary ?? null,
          createdAtUtc: a.createdAtUtc,
        },
        supersedesPriorByName: a.supersedesPriorByName ?? false,
      };
    }
    case 'done':
      return { kind: 'done' };
    case 'error': {
      const errPayload = payload as { code?: string | null; message?: string };
      return {
        kind: 'error',
        code: typeof errPayload.code === 'string' && errPayload.code.length > 0 ? errPayload.code : null,
        message: errPayload.message ?? 'Unknown error',
      };
    }
    default:
      return null;
  }
}
