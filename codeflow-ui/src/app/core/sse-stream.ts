import { Observable } from 'rxjs';

export interface SseFrame {
  eventName: string;
  dataLines: string[];
}

/**
 * sc-525 — Per-call retry policy for transient network failures (TypeError from fetch:
 * `ERR_HTTP2_PROTOCOL_ERROR`, dropped TLS, mid-handshake reset). Caller opts in by passing
 * a non-zero `attempts` budget. Retries are only attempted before any frame has been
 * surfaced to the subscriber on the current attempt — once we've delivered data, the
 * downstream pipeline is committed to a turn and a silent re-POST would either duplicate
 * (without server idempotency) or replay (with it). We reset the per-attempt
 * "frames-emitted" flag every time, but the budget itself is a one-shot per call.
 *
 * `delaysMs` is the backoff schedule between attempts. Defaults to `[250, 1000, 3000]`
 * which gives ~4.25 s of total recovery before surfacing the error. Server-side errors
 * (any HTTP response, including 5xx) are NOT retried — those are application-level and
 * may be expensive to repeat.
 */
export interface StreamSseRetryPolicy {
  attempts: number;
  delaysMs?: readonly number[];
}

export interface StreamSseOptions extends Omit<RequestInit, 'headers' | 'signal'> {
  headers?: Record<string, string>;
  accessToken?: string | null | Promise<string | null>;
  handleErrorResponse?: (response: Response) => Promise<unknown | null>;
  retry?: StreamSseRetryPolicy;
}

const DEFAULT_RETRY_DELAYS_MS: readonly number[] = [250, 1000, 3000];

export function streamSse<T>(
  url: string,
  options: StreamSseOptions,
  parseFrame: (frame: SseFrame) => T | null,
): Observable<T> {
  return new Observable<T>(subscriber => {
    const controller = new AbortController();

    const run = async () => {
      const retryAttempts = options.retry?.attempts ?? 0;
      const retryDelays = options.retry?.delaysMs ?? DEFAULT_RETRY_DELAYS_MS;
      let attemptIndex = 0;

      while (true) {
        let framesEmittedThisAttempt = false;
        try {
          const { handleErrorResponse, retry: _retry, ...requestInit } = options;
          delete requestInit.accessToken;

          const headers = await buildHeaders(options);
          const response = await fetch(url, {
            ...requestInit,
            headers,
            signal: controller.signal,
          });

          if (!response.ok || !response.body) {
            if (handleErrorResponse) {
              const fallbackEvent = await handleErrorResponse(response);
              if (fallbackEvent) {
                subscriber.next(fallbackEvent as T);
                subscriber.complete();
                return;
              }
              // Helper didn't recognize the response — fall through to the default error path
              // so callers that opt in for one specific error code (e.g. sc-274 preflight 422)
              // still see the generic HTTP error for everything else.
            }

            // Server responses are not retried — they're application-level and the body
            // (validation details, 4xx semantics, 5xx token cap, etc.) is the answer.
            subscriber.error(new Error(`HTTP ${response.status} ${response.statusText}`));
            return;
          }

          await readSseBody(response.body, raw => {
            const parsed = parseFrame(parseRawSseFrame(raw));
            if (parsed) {
              framesEmittedThisAttempt = true;
              subscriber.next(parsed);
            }
          });

          subscriber.complete();
          return;
        } catch (err) {
          if (controller.signal.aborted || (err as Error)?.name === 'AbortError') {
            subscriber.complete();
            return;
          }

          // Retry only on transient transport failures (TypeError from fetch: network
          // refused, ERR_HTTP2_PROTOCOL_ERROR, etc.) AND only when no frames have been
          // surfaced this attempt. Once any frame has been delivered downstream, replaying
          // would mean re-emitting events that already advanced the consumer state — leave
          // that to the server-side idempotency layer (sc-525) by surfacing the error.
          const isTransport = err instanceof TypeError;
          const canRetry = isTransport
            && !framesEmittedThisAttempt
            && attemptIndex < retryAttempts;
          if (!canRetry) {
            subscriber.error(err);
            return;
          }

          const delayMs = retryDelays[Math.min(attemptIndex, retryDelays.length - 1)] ?? 0;
          attemptIndex += 1;
          try {
            await sleep(delayMs, controller.signal);
          } catch {
            subscriber.complete();
            return;
          }
          // Loop falls through to the next attempt.
        }
      }
    };

    void run();

    return () => controller.abort();
  });
}

async function buildHeaders(options: StreamSseOptions): Promise<Record<string, string>> {
  const headers: Record<string, string> = {
    Accept: 'text/event-stream',
    ...options.headers,
  };

  const token = await Promise.resolve(options.accessToken);
  if (token) {
    headers['Authorization'] = `Bearer ${token}`;
  }

  return headers;
}

async function readSseBody(
  body: ReadableStream<Uint8Array>,
  onFrame: (raw: string) => void,
): Promise<void> {
  const reader = body.getReader();
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
      onFrame(raw);
      delimiterIndex = buffer.indexOf('\n\n');
    }
  }
}

function parseRawSseFrame(raw: string): SseFrame {
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

  return { eventName, dataLines };
}

function sleep(ms: number, signal: AbortSignal): Promise<void> {
  if (ms <= 0) {
    return Promise.resolve();
  }
  return new Promise<void>((resolve, reject) => {
    if (signal.aborted) {
      reject(new DOMException('Aborted', 'AbortError'));
      return;
    }
    const timer = setTimeout(() => {
      signal.removeEventListener('abort', onAbort);
      resolve();
    }, ms);
    const onAbort = () => {
      clearTimeout(timer);
      signal.removeEventListener('abort', onAbort);
      reject(new DOMException('Aborted', 'AbortError'));
    };
    signal.addEventListener('abort', onAbort, { once: true });
  });
}
