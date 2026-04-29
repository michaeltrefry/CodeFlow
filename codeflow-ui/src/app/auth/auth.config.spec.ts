import { buildAuthConfig, hasAuthConfigured } from './auth.config';
import { loadRuntimeConfig } from '../core/runtime-config';

describe('auth config', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('builds OAuth config from runtime config and current browser origin', async () => {
    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      oauth: {
        authority: 'https://id.example.test/realms/codeflow',
        clientId: 'codeflow-browser',
        scope: 'openid profile email offline_access',
      },
    })));
    await loadRuntimeConfig();

    expect(hasAuthConfigured()).toBe(true);
    expect(buildAuthConfig()).toMatchObject({
      issuer: 'https://id.example.test/realms/codeflow',
      clientId: 'codeflow-browser',
      redirectUri: window.location.origin,
      responseType: 'code',
      scope: 'openid profile email offline_access',
      showDebugInformation: false,
      requireHttps: false,
    });
  });

  it('reports auth unconfigured when runtime authority is empty', async () => {
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockRejectedValue(new Error('offline')));

    await loadRuntimeConfig();

    expect(hasAuthConfigured()).toBe(false);
  });
});

function jsonResponse(body: unknown): Response {
  return {
    ok: true,
    status: 200,
    json: vi.fn().mockResolvedValue(body),
  } as unknown as Response;
}
