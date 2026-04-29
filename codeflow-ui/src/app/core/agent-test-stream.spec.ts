import { streamAgentTest, type AgentTestEvent, type AgentTestRequest } from './agent-test-stream';

describe('streamAgentTest', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('posts the agent test request and parses chunked SSE lifecycle events', async () => {
    const request: AgentTestRequest = {
      agentKey: 'reviewer',
      agentVersion: 3,
      input: 'Review this',
      variables: { customer: 'Acme' },
    };
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      sseResponse([
        'event: started\ndata: {"agentKey":"reviewer","agentVersion":3,"provider":"openai",',
        '"model":"gpt-test","timestampUtc":"2026-04-29T00:00:00Z"}\n\n',
        ': heartbeat\n\n',
        'event: model-call-completed\ndata: {"roundNumber":1,"assistantText":"Done","toolCallCount":0,',
        '"callTokenUsage":{"inputTokens":4,"outputTokens":2,"totalTokens":6},',
        '"timestampUtc":"2026-04-29T00:00:01Z"}\n\n',
      ]),
    );
    vi.stubGlobal('fetch', fetchMock);

    const events = await collect(streamAgentTest(request, Promise.resolve('agent-token')));

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/agent-test',
      expect.objectContaining({
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Accept: 'text/event-stream',
          Authorization: 'Bearer agent-token',
        },
        body: JSON.stringify(request),
      }),
    );
    expect(events).toEqual<AgentTestEvent[]>([
      {
        type: 'started',
        agentKey: 'reviewer',
        agentVersion: 3,
        provider: 'openai',
        model: 'gpt-test',
        timestampUtc: '2026-04-29T00:00:00Z',
      },
      {
        type: 'model-call-completed',
        roundNumber: 1,
        assistantText: 'Done',
        toolCallCount: 0,
        callTokenUsage: { inputTokens: 4, outputTokens: 2, totalTokens: 6 },
        timestampUtc: '2026-04-29T00:00:01Z',
      },
    ]);
  });

  it('turns HTTP failures into terminal error events', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue({
        ok: false,
        status: 400,
        statusText: 'Bad Request',
        body: null,
        text: vi.fn().mockResolvedValue('Prompt template failed'),
      } as unknown as Response),
    );

    const events = await collect(streamAgentTest({ agentKey: 'bad', input: '' }, null));

    expect(events).toEqual<AgentTestEvent[]>([
      { type: 'error', message: 'Prompt template failed' },
    ]);
  });

  it('ignores comments and malformed JSON frames', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        sseResponse([
          ': keep-alive\n\n',
          'event: started\ndata: not-json\n\n',
          'event: completed\ndata: {"output":"Final","decisionKind":"Completed","toolCallsExecuted":0,',
          '"durationMs":12,"timestampUtc":"2026-04-29T00:00:02Z"}\n\n',
        ]),
      ),
    );

    const events = await collect(streamAgentTest({ agentKey: 'reviewer', input: 'Hi' }, null));

    expect(events).toEqual<AgentTestEvent[]>([
      {
        type: 'completed',
        output: 'Final',
        decisionKind: 'Completed',
        toolCallsExecuted: 0,
        durationMs: 12,
        timestampUtc: '2026-04-29T00:00:02Z',
      },
    ]);
  });
});

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
