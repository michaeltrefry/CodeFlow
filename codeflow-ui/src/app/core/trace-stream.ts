import { Observable } from 'rxjs';
import { TraceStreamEvent } from './models';
import { AuthService } from '../auth/auth.service';

/**
 * Subscribes to /api/traces/{id}/stream using a fetch-based SSE reader so the
 * Authorization header can be attached (EventSource cannot set custom headers
 * and bypasses the Angular auth interceptor, so streaming is broken under JWT).
 */
export function streamTrace(
  traceId: string,
  auth: AuthService,
): Observable<TraceStreamEvent> {
  return new Observable<TraceStreamEvent>(subscriber => {
    const controller = new AbortController();

    const run = async () => {
      try {
        const headers: Record<string, string> = {
          Accept: 'text/event-stream',
        };
        const token = await auth.getValidAccessToken();
        if (token) {
          headers['Authorization'] = `Bearer ${token}`;
        }

        const response = await fetch(`/api/traces/${encodeURIComponent(traceId)}/stream`, {
          method: 'GET',
          headers,
          signal: controller.signal,
        });

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

function parseSseFrame(raw: string): TraceStreamEvent | null {
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

  if (eventName !== 'requested' && eventName !== 'completed' && eventName !== 'tokenusagerecorded') {
    return null;
  }
  if (dataLines.length === 0) {
    return null;
  }

  try {
    return JSON.parse(dataLines.join('\n')) as TraceStreamEvent;
  } catch {
    return null;
  }
}
