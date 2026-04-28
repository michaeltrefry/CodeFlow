import { Observable } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { AssistantMessage } from './assistant.api';

/**
 * One frame from the assistant SSE stream. Mirrors the event names emitted by
 * <c>AssistantEndpoints.WriteEventAsync</c> in the API:
 *   - <c>user-message-persisted</c>: full user-turn message row, with stable id + sequence.
 *   - <c>text-delta</c>: incremental assistant content; concatenate to build the running text.
 *   - <c>token-usage</c>: provider-reported usage for the just-completed turn.
 *   - <c>assistant-message-persisted</c>: full assistant-turn message row, with finalized
 *     content + provider/model/invocationId.
 *   - <c>done</c>: terminal — stream is closing.
 *   - <c>error</c>: terminal failure with a free-form message.
 */
export type AssistantStreamEvent =
  | { kind: 'user-message-persisted'; message: AssistantMessage }
  | { kind: 'text-delta'; delta: string }
  | { kind: 'token-usage'; recordId: string; provider: string; model: string; usage: unknown }
  | { kind: 'assistant-message-persisted'; message: AssistantMessage }
  | { kind: 'done' }
  | { kind: 'error'; message: string };

/**
 * Streams a single chat turn against <c>POST /api/assistant/conversations/{id}/messages</c>.
 * Mirrors the fetch+ReadableStream pattern from {@link streamTrace} so the Authorization
 * header can be attached (EventSource cannot set custom headers and bypasses the auth
 * interceptor).
 */
export function streamAssistantTurn(
  conversationId: string,
  content: string,
  auth: AuthService,
): Observable<AssistantStreamEvent> {
  return new Observable<AssistantStreamEvent>(subscriber => {
    const controller = new AbortController();

    const run = async () => {
      try {
        const headers: Record<string, string> = {
          Accept: 'text/event-stream',
          'Content-Type': 'application/json',
        };
        const token = await auth.getValidAccessToken();
        if (token) {
          headers['Authorization'] = `Bearer ${token}`;
        }

        const response = await fetch(
          `/api/assistant/conversations/${encodeURIComponent(conversationId)}/messages`,
          {
            method: 'POST',
            headers,
            body: JSON.stringify({ content }),
            signal: controller.signal,
          },
        );

        if (!response.ok || !response.body) {
          subscriber.error(new Error(`HTTP ${response.status} ${response.statusText}`));
          return;
        }

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        while (true) {
          const { value, done } = await reader.read();
          if (done) {
            break;
          }
          buffer += decoder.decode(value, { stream: true });

          let delimiterIndex = buffer.indexOf('\n\n');
          while (delimiterIndex !== -1) {
            const raw = buffer.slice(0, delimiterIndex);
            buffer = buffer.slice(delimiterIndex + 2);

            const parsed = parseSseFrame(raw);
            if (parsed) {
              subscriber.next(parsed);
            }
            delimiterIndex = buffer.indexOf('\n\n');
          }
        }

        subscriber.complete();
      } catch (err) {
        if ((err as Error)?.name === 'AbortError') {
          subscriber.complete();
          return;
        }
        subscriber.error(err);
      }
    };

    void run();

    return () => controller.abort();
  });
}

function parseSseFrame(raw: string): AssistantStreamEvent | null {
  let eventName = '';
  const dataLines: string[] = [];

  for (const line of raw.split('\n')) {
    if (line.startsWith(':')) {
      continue;
    }
    if (line.startsWith('event:')) {
      eventName = line.slice(6).trim();
    } else if (line.startsWith('data:')) {
      dataLines.push(line.slice(5).trim());
    }
  }

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
      const u = payload as { recordId: string; provider: string; model: string; usage: unknown };
      return { kind: 'token-usage', recordId: u.recordId, provider: u.provider, model: u.model, usage: u.usage };
    }
    case 'assistant-message-persisted':
      return { kind: 'assistant-message-persisted', message: payload as AssistantMessage };
    case 'done':
      return { kind: 'done' };
    case 'error':
      return { kind: 'error', message: (payload as { message: string }).message ?? 'Unknown error' };
    default:
      return null;
  }
}
