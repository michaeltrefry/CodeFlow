import type { AuthService } from '../auth/auth.service';
import type { AssistantMessage } from './assistant.api';
import { streamAssistantTurn, type AssistantStreamEvent } from './assistant-stream';

describe('streamAssistantTurn', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('posts an authenticated turn and parses chunked assistant SSE events', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(
      sseResponse([
        ': keep-alive\n\n',
        'event: text-delta\ndata: {"delta":"Hel',
        'lo"}\n\n',
        'event: tool-result\ndata: {"id":"tool-1","name":"lookup","result":"done"}\n\n',
        'event: done\ndata: {}\n\n',
      ]),
    );
    vi.stubGlobal('fetch', fetchMock);

    const events = await collect(
      streamAssistantTurn(
        'conversation/1',
        'Tell me',
        authWithToken('access-token'),
        { pageContext: { kind: 'other', route: '/workflows' } },
      ),
    );

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/assistant/conversations/conversation%2F1/messages',
      expect.objectContaining({
        method: 'POST',
        headers: {
          Accept: 'text/event-stream',
          'Content-Type': 'application/json',
          Authorization: 'Bearer access-token',
        },
        body: JSON.stringify({
          content: 'Tell me',
          pageContext: { kind: 'other', route: '/workflows' },
        }),
      }),
    );
    expect(events).toEqual<AssistantStreamEvent[]>([
      { kind: 'text-delta', delta: 'Hello' },
      { kind: 'tool-result', id: 'tool-1', name: 'lookup', result: 'done', isError: false },
      { kind: 'done' },
    ]);
  });

  it('ignores malformed frames while preserving persisted message events', async () => {
    const message: AssistantMessage = {
      id: 'message-1',
      sequence: 2,
      role: 'assistant',
      content: 'Final',
      provider: 'openai',
      model: 'gpt-test',
      invocationId: 'invocation-1',
      createdAtUtc: '2026-04-29T00:00:00Z',
    };
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        sseResponse([
          'event: unknown\ndata: {"ok":true}\n\n',
          'event: text-delta\ndata: not-json\n\n',
          `event: assistant-message-persisted\ndata: ${JSON.stringify(message)}\n\n`,
        ]),
      ),
    );

    const events = await collect(streamAssistantTurn('conversation-1', 'Hello', authWithToken(null)));

    expect(events).toEqual<AssistantStreamEvent[]>([
      { kind: 'assistant-message-persisted', message },
    ]);
  });

  it('parses conversation-compacted frames with the persisted summary message and reset totals', async () => {
    const summary: AssistantMessage = {
      id: 'summary-1',
      sequence: 5,
      role: 'summary',
      content: 'Earlier turns synthesized into one paragraph.',
      provider: 'anthropic',
      model: 'claude',
      invocationId: null,
      createdAtUtc: '2026-04-29T16:00:00Z',
    };
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue(
        sseResponse([
          `event: conversation-compacted\ndata: ${JSON.stringify({
            summary,
            compactedThroughSequence: 4,
            conversationInputTokensTotal: 0,
            conversationOutputTokensTotal: 0,
          })}\n\n`,
        ]),
      ),
    );

    const events = await collect(streamAssistantTurn('conversation-1', 'Hello', authWithToken(null)));

    expect(events).toEqual<AssistantStreamEvent[]>([
      {
        kind: 'conversation-compacted',
        summary,
        compactedThroughSequence: 4,
        conversationInputTokensTotal: 0,
        conversationOutputTokensTotal: 0,
      },
    ]);
  });

  it('surfaces non-ok responses as observable errors', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue({
        ok: false,
        status: 401,
        statusText: 'Unauthorized',
        body: null,
      } as Response),
    );

    await expect(collect(streamAssistantTurn('conversation-1', 'Hello', authWithToken(null))))
      .rejects.toThrow('HTTP 401 Unauthorized');
  });
});

function authWithToken(token: string | null): AuthService {
  return {
    getValidAccessToken: vi.fn().mockResolvedValue(token),
  } as unknown as AuthService;
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
