import { streamSse, type StreamSseOptions } from './sse-stream';

describe('streamSse', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('completes normally when fetch succeeds on the first attempt', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      sseResponse(['event: hello\ndata: {}\n\n']),
    );
    vi.stubGlobal('fetch', fetchMock);

    const events = await collect(streamFor('/api/x', { method: 'POST' }));

    expect(events).toEqual([{ eventName: 'hello', dataLines: ['{}'] }]);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('does NOT retry on TypeError when no retry policy is configured', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockRejectedValue(new TypeError('network is fucked'));
    vi.stubGlobal('fetch', fetchMock);

    await expect(collect(streamFor('/api/x', { method: 'POST' }))).rejects.toThrow('network is fucked');
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('retries up to the configured budget on TypeError, then surfaces the original error', async () => {
    vi.useFakeTimers();
    const err = new TypeError('ERR_HTTP2_PROTOCOL_ERROR');
    const fetchMock = vi.fn<typeof fetch>().mockRejectedValue(err);
    vi.stubGlobal('fetch', fetchMock);

    const promise = collect(
      streamFor('/api/x', { method: 'POST', retry: { attempts: 2, delaysMs: [10, 20] } }),
    ).catch(e => e);

    await vi.runAllTimersAsync();
    const result = await promise;

    expect(result).toBe(err);
    // 1 original + 2 retries = 3 attempts
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });

  it('retry succeeds when a later attempt returns a real SSE stream', async () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn<typeof fetch>()
      .mockRejectedValueOnce(new TypeError('first attempt died'))
      .mockResolvedValueOnce(sseResponse(['event: ok\ndata: {}\n\n']));
    vi.stubGlobal('fetch', fetchMock);

    const promise = collect(streamFor('/api/x', { method: 'POST', retry: { attempts: 1, delaysMs: [5] } }));
    await vi.runAllTimersAsync();
    const events = await promise;

    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(events).toEqual([{ eventName: 'ok', dataLines: ['{}'] }]);
  });

  it('does NOT retry when the server returned a non-ok HTTP response', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue({
      ok: false,
      status: 500,
      statusText: 'Internal Server Error',
      body: null,
    } as Response);
    vi.stubGlobal('fetch', fetchMock);

    await expect(collect(streamFor('/api/x', {
      method: 'POST',
      retry: { attempts: 3, delaysMs: [1, 1, 1] },
    }))).rejects.toThrow('HTTP 500 Internal Server Error');
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('does NOT retry once any frame has been delivered downstream', async () => {
    // Connection drops MID-stream after we've already pushed one frame to the consumer.
    // Re-running the request would re-emit those frames; defer that responsibility to the
    // server-side idempotency layer (which the assistant-stream wrapper opts into separately).
    const dropAfterFirstFrame = (): Response => {
      const encoder = new TextEncoder();
      let pulled = 0;
      return {
        ok: true,
        status: 200,
        statusText: 'OK',
        body: new ReadableStream<Uint8Array>({
          pull(controller) {
            if (pulled === 0) {
              controller.enqueue(encoder.encode('event: text-delta\ndata: {"delta":"hi"}\n\n'));
              pulled += 1;
            } else {
              controller.error(new TypeError('mid-stream drop'));
            }
          },
        }),
      } as Response;
    };

    const fetchMock = vi.fn<typeof fetch>().mockImplementation(() => Promise.resolve(dropAfterFirstFrame()));
    vi.stubGlobal('fetch', fetchMock);

    const observable = streamFor('/api/x', {
      method: 'POST',
      retry: { attempts: 5, delaysMs: [1, 1, 1, 1, 1] },
    });
    const events: { eventName: string; dataLines: string[] }[] = [];
    const err = await new Promise<unknown>(resolve => {
      observable.subscribe({
        next: e => events.push(e),
        error: resolve,
        complete: () => resolve(null),
      });
    });

    expect(events).toHaveLength(1);
    expect(err).toBeInstanceOf(TypeError);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});

function streamFor(url: string, options: StreamSseOptions) {
  return streamSse(url, options, frame => frame);
}

function sseResponse(chunks: string[]): Response {
  const encoder = new TextEncoder();
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    body: new ReadableStream<Uint8Array>({
      start(controller) {
        for (const chunk of chunks) {
          controller.enqueue(encoder.encode(chunk));
        }
        controller.close();
      },
    }),
  } as Response;
}

function collect<T>(observable: { subscribe: (o: {
  next: (v: T) => void;
  error: (e: unknown) => void;
  complete: () => void;
}) => unknown }): Promise<T[]> {
  return new Promise<T[]>((resolve, reject) => {
    const values: T[] = [];
    observable.subscribe({
      next: v => values.push(v),
      error: reject,
      complete: () => resolve(values),
    });
  });
}
