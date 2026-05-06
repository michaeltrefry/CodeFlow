import { generateIdempotencyKey } from './chat-panel.component';
import { parseSseFrame } from '../../core/assistant-stream';

/**
 * sc-525 — Spec for the chat-panel's per-turn idempotency-key generator. The full
 * sendMessage/retryLastTurn/cancelTurn lifecycle is covered end-to-end by the server-side
 * integration tests (a duplicate POST with the same key replays recorded events without a
 * second LLM call). What we want here is a focused unit test for the generator itself —
 * specifically that the fallback path produces a key the server's validator accepts.
 */
describe('chat-panel idempotency key generator', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('returns a unique value on each invocation', () => {
    const a = generateIdempotencyKey();
    const b = generateIdempotencyKey();
    expect(a).not.toBe(b);
  });

  it('produces UUID-shaped output via crypto.randomUUID when available', () => {
    const stub = vi.fn(() => '550e8400-e29b-41d4-a716-446655440000');
    vi.stubGlobal('crypto', { randomUUID: stub, getRandomValues: () => { throw new Error('nope'); } });

    const key = generateIdempotencyKey();

    expect(key).toBe('550e8400-e29b-41d4-a716-446655440000');
    expect(stub).toHaveBeenCalledTimes(1);
  });

  it('falls back to getRandomValues + UUID-shape formatting when randomUUID is missing', () => {
    // Older browsers / test environments where crypto.randomUUID isn't defined still need to
    // produce a server-acceptable key. The fallback path must yield a UUID-ish string the
    // backend's [A-Za-z0-9_-]{8,128} validator accepts.
    let counter = 0;
    vi.stubGlobal('crypto', {
      // randomUUID intentionally omitted so the fallback branch fires.
      getRandomValues: (buf: Uint8Array) => {
        for (let i = 0; i < buf.length; i++) {
          buf[i] = counter++ & 0xff;
        }
        return buf;
      },
    });

    const key = generateIdempotencyKey();

    expect(key).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
  });

  it('still returns a key when neither randomUUID nor getRandomValues is available', () => {
    // Last-resort Math.random branch — must still produce a server-acceptable key. We only
    // check shape + length here; "random enough" is implicit in the implementation.
    vi.stubGlobal('crypto', {});

    const key = generateIdempotencyKey();

    expect(key).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
    expect(key.length).toBeGreaterThanOrEqual(8);
    expect(key.length).toBeLessThanOrEqual(128);
  });
});

/**
 * sc-806 — Banner UX: when the server emits a structured SSE error frame with the
 * `turn-still-running` code, the chat panel must surface BOTH Retry and Cancel. We test
 * the SSE parser end of that contract here (pure function, deterministic). The "renders
 * both buttons" half is exercised by the component-level test in CodeFlow.Api.Tests
 * through the chat-panel.component.ts template binding on `canCancelTurnFromBanner()`.
 */
describe('parseSseFrame error frame (sc-806 structured payload)', () => {
  function errorFrame(body: unknown) {
    return parseSseFrame({ eventName: 'error', dataLines: [JSON.stringify(body)] });
  }

  it('extracts the turn-still-running code from the new payload shape', () => {
    const frame = errorFrame({
      code: 'turn-still-running',
      message: 'Your previous turn is still running on the server. Wait a few seconds and retry, or cancel to start fresh.',
    });

    expect(frame).toEqual({
      kind: 'error',
      code: 'turn-still-running',
      message: 'Your previous turn is still running on the server. Wait a few seconds and retry, or cancel to start fresh.',
    });
  });

  it('extracts the live-tail-fell-behind and live-tail-timeout codes the same way', () => {
    expect(errorFrame({ code: 'live-tail-fell-behind', message: 'x' }))
      .toMatchObject({ kind: 'error', code: 'live-tail-fell-behind' });
    expect(errorFrame({ code: 'live-tail-timeout', message: 'x' }))
      .toMatchObject({ kind: 'error', code: 'live-tail-timeout' });
  });

  it('returns code: null when the server omits it (older deployments)', () => {
    // Pre-AR-4 servers emit `{ message }` without a code — clients must treat that as a
    // generic error (Retry only, no Cancel) so the banner doesn't show a stale affordance.
    expect(errorFrame({ message: 'Something broke' }))
      .toEqual({ kind: 'error', code: null, message: 'Something broke' });
  });

  it('treats a non-string code as null', () => {
    // Defensive parse: a malformed/numeric `code` shouldn't trip the banner branch logic.
    expect(errorFrame({ code: 42, message: 'msg' }))
      .toEqual({ kind: 'error', code: null, message: 'msg' });
  });

  it('treats an empty-string code as null so equality checks against known codes still fail', () => {
    expect(errorFrame({ code: '', message: 'msg' }))
      .toEqual({ kind: 'error', code: null, message: 'msg' });
  });
});
