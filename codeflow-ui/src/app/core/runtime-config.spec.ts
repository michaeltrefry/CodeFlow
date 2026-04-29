import { getRuntimeConfig, loadRuntimeConfig } from './runtime-config';

describe('runtime config', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('loads OAuth settings from runtime-config.json with cache-busting request options', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      oauth: {
        authority: 'https://id.example.test/realms/codeflow',
        clientId: 'codeflow-browser',
        scope: 'openid profile email offline_access',
      },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const loaded = await loadRuntimeConfig();

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringMatching(/^\/runtime-config\.json\?ts=\d+$/),
      { cache: 'no-store', credentials: 'omit' },
    );
    expect(loaded).toEqual({
      oauth: {
        authority: 'https://id.example.test/realms/codeflow',
        clientId: 'codeflow-browser',
        scope: 'openid profile email offline_access',
      },
    });
    expect(getRuntimeConfig()).toBe(loaded);
  });

  it('merges partial config with defaults and rejects empty client or scope overrides', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      oauth: {
        authority: 'https://id.example.test/realms/codeflow',
        clientId: '',
        scope: '',
      },
    })));

    await loadRuntimeConfig();

    expect(getRuntimeConfig()).toEqual({
      oauth: {
        authority: 'https://id.example.test/realms/codeflow',
        clientId: 'codeflow-ui',
        scope: 'openid profile email',
      },
    });
  });

  it('resets to defaults after a failed runtime-config fetch', async () => {
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      oauth: {
        authority: 'https://id.example.test/realms/codeflow',
        clientId: 'configured-client',
        scope: 'openid',
      },
    })));
    await loadRuntimeConfig();

    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue({
      ok: false,
      status: 503,
      json: vi.fn(),
    } as unknown as Response));

    const loaded = await loadRuntimeConfig();

    expect(loaded).toEqual({
      oauth: {
        authority: '',
        clientId: 'codeflow-ui',
        scope: 'openid profile email',
      },
    });
    expect(getRuntimeConfig()).toEqual(loaded);
  });
});

function jsonResponse(body: unknown): Response {
  return {
    ok: true,
    status: 200,
    json: vi.fn().mockResolvedValue(body),
  } as unknown as Response;
}
