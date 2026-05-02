import { generateIdempotencyKey } from './chat-panel.component';

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
