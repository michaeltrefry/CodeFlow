import type { AuthService } from '../auth/auth.service';
import type { TraceStreamEvent } from './models';
import { streamTrace } from './trace-stream';

describe('streamTrace', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('opens an authenticated trace stream and parses supported event frames', async () => {
    const requested = traceEvent({ kind: 'Requested', roundId: 'round-1' });
    const tokenUsageRecorded = traceEvent({
      kind: 'TokenUsageRecorded',
      roundId: 'round-1',
      tokenUsage: {
        recordId: 'usage-1',
        nodeId: 'node-1',
        invocationId: 'invocation-1',
        scopeChain: ['root'],
        provider: 'openai',
        model: 'gpt-test',
        usage: { input_tokens: 7, output_tokens: 3 },
      },
    });
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      sseResponse([
        'event: requested\ndata: ',
        `${JSON.stringify(requested)}\n\n`,
        'event: noise\ndata: {"ignored":true}\n\n',
        `event: tokenusagerecorded\ndata: ${JSON.stringify(tokenUsageRecorded)}\n\n`,
      ]),
    );
    vi.stubGlobal('fetch', fetchMock);

    const events = await collect(streamTrace('trace/1', authWithToken('trace-token')));

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/traces/trace%2F1/stream',
      expect.objectContaining({
        method: 'GET',
        headers: {
          Accept: 'text/event-stream',
          Authorization: 'Bearer trace-token',
        },
      }),
    );
    expect(events).toEqual<TraceStreamEvent[]>([requested, tokenUsageRecorded]);
  });

  it('drops malformed JSON and frames without data', async () => {
    const completed = traceEvent({ kind: 'Completed', decision: 'Approved' });
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        sseResponse([
          'event: completed\n\n',
          'event: requested\ndata: not-json\n\n',
          `event: completed\ndata: ${JSON.stringify(completed)}\n\n`,
        ]),
      ),
    );

    const events = await collect(streamTrace('trace-1', authWithToken(null)));

    expect(events).toEqual<TraceStreamEvent[]>([completed]);
  });

  it('surfaces non-ok responses as observable errors', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue({
        ok: false,
        status: 404,
        statusText: 'Not Found',
        body: null,
      } as Response),
    );

    await expect(collect(streamTrace('missing-trace', authWithToken(null))))
      .rejects.toThrow('HTTP 404 Not Found');
  });
});

function authWithToken(token: string | null): AuthService {
  return {
    getValidAccessToken: vi.fn().mockResolvedValue(token),
  } as unknown as AuthService;
}

function traceEvent(overrides: Partial<TraceStreamEvent> = {}): TraceStreamEvent {
  return {
    traceId: 'trace-1',
    roundId: 'round-1',
    kind: 'Requested',
    agentKey: 'reviewer',
    agentVersion: 1,
    timestampUtc: '2026-04-29T00:00:00Z',
    ...overrides,
  };
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

function collect<T>(observable: { subscribe: (observer: {
  next: (value: T) => void;
  error: (err: unknown) => void;
  complete: () => void;
}) => unknown }): Promise<T[]> {
  return new Promise<T[]>((resolve, reject) => {
    const values: T[] = [];
    observable.subscribe({
      next: value => values.push(value),
      error: reject,
      complete: () => resolve(values),
    });
  });
}
