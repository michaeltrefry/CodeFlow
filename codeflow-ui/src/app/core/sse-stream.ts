import { Observable } from 'rxjs';

export interface SseFrame {
  eventName: string;
  dataLines: string[];
}

export interface StreamSseOptions extends Omit<RequestInit, 'headers' | 'signal'> {
  headers?: Record<string, string>;
  accessToken?: string | null | Promise<string | null>;
  handleErrorResponse?: (response: Response) => Promise<unknown | null>;
}

export function streamSse<T>(
  url: string,
  options: StreamSseOptions,
  parseFrame: (frame: SseFrame) => T | null,
): Observable<T> {
  return new Observable<T>(subscriber => {
    const controller = new AbortController();

    const run = async () => {
      try {
        const { handleErrorResponse, ...requestInit } = options;
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
          }

          subscriber.error(new Error(`HTTP ${response.status} ${response.statusText}`));
          return;
        }

        await readSseBody(response.body, raw => {
          const parsed = parseFrame(parseRawSseFrame(raw));
          if (parsed) {
            subscriber.next(parsed);
          }
        });

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
