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

  // sc-274 phase 2 — assistant-preflight-ambiguous 422 short-circuits the SSE stream and is
  // surfaced as a synthetic preflight-refused event so the chat panel renders the
  // clarification banner instead of going through the generic error path.
  it('parses 422 assistant-preflight-ambiguous response into a preflight-refused event', async () => {
    const refusalBody = {
      conversationId: 'conv-42',
      code: 'assistant-preflight-ambiguous',
      mode: 'AssistantChat',
      overallScore: 0.2,
      threshold: 0.4,
      dimensions: [
        { dimension: 'goal', score: 0.2, reason: 'action verb without scope' },
        { dimension: 'constraints', score: 1.0, reason: null },
        { dimension: 'success_criteria', score: 1.0, reason: null },
        { dimension: 'context', score: 1.0, reason: null },
      ],
      missingFields: ['content.scope'],
      clarificationQuestions: ['What specifically should I fix?'],
    };
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue({
        ok: false,
        status: 422,
        statusText: 'Unprocessable Entity',
        body: null,
        json: () => Promise.resolve(refusalBody),
      } as unknown as Response),
    );

    const events = await collect(streamAssistantTurn('conv-42', 'fix it', authWithToken(null)));

    expect(events).toEqual<AssistantStreamEvent[]>([
      {
        kind: 'preflight-refused',
        payload: {
          conversationId: 'conv-42',
          code: 'assistant-preflight-ambiguous',
          mode: 'AssistantChat',
          overallScore: 0.2,
          threshold: 0.4,
          dimensions: refusalBody.dimensions,
          missingFields: ['content.scope'],
          clarificationQuestions: ['What specifically should I fix?'],
        },
      },
    ]);
  });

  it('falls through to the error path when a 422 lacks the assistant-preflight-ambiguous code', async () => {
    // Validation 422s (e.g. ProblemDetails-shape from a malformed request) must not be
    // mistaken for a preflight refusal — the banner would then render with empty fields.
    vi.stubGlobal(
      'fetch',
      vi.fn<typeof fetch>().mockResolvedValue({
        ok: false,
        status: 422,
        statusText: 'Unprocessable Entity',
        body: null,
        json: () => Promise.resolve({ status: 422, detail: 'validation failed' }),
      } as unknown as Response),
    );

    await expect(collect(streamAssistantTurn('conv-1', 'fix it', authWithToken(null))))
      .rejects.toThrow('HTTP 422 Unprocessable Entity');
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
