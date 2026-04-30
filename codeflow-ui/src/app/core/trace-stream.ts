import type { Observable } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { TraceStreamEvent } from './models';
import type { SseFrame } from './sse-stream';
import { streamSse } from './sse-stream';

/**
 * Subscribes to /api/traces/{id}/stream using a fetch-based SSE reader so the
 * Authorization header can be attached (EventSource cannot set custom headers
 * and bypasses the Angular auth interceptor, so streaming is broken under JWT).
 */
export function streamTrace(
  traceId: string,
  auth: AuthService,
): Observable<TraceStreamEvent> {
  return streamSse(
    `/api/traces/${encodeURIComponent(traceId)}/stream`,
    {
      method: 'GET',
      accessToken: auth.getValidAccessToken(),
    },
    parseSseFrame,
  );
}

function parseSseFrame({ eventName, dataLines }: SseFrame): TraceStreamEvent | null {
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
