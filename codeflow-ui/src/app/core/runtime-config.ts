import { consoleLogger } from './logger.service';

export interface OAuthRuntimeConfig {
  authority: string;
  clientId: string;
  scope: string;
}

export interface RuntimeConfig {
  oauth: OAuthRuntimeConfig;
}

const DEFAULT_CONFIG: RuntimeConfig = {
  oauth: {
    authority: '',
    clientId: 'codeflow-ui',
    scope: 'openid profile email'
  }
};

let loaded: RuntimeConfig = DEFAULT_CONFIG;

export async function loadRuntimeConfig(): Promise<RuntimeConfig> {
  try {
    // Cache-bust because nginx serves runtime-config.json with no-store, but a stale browser
    // cache from a prior deploy could still hand back an old issuer.
    const response = await fetch(`/runtime-config.json?ts=${Date.now()}`, {
      cache: 'no-store',
      credentials: 'omit'
    });
    if (!response.ok) {
      consoleLogger.warn(`[runtime-config] fetch failed: ${response.status}; using defaults`);
      loaded = DEFAULT_CONFIG;
      return DEFAULT_CONFIG;
    }
    const parsed = await response.json() as Partial<RuntimeConfig>;
    loaded = mergeWithDefaults(parsed);
    return loaded;
  } catch (err) {
    consoleLogger.warn('[runtime-config] fetch threw; using defaults', err);
    loaded = DEFAULT_CONFIG;
    return loaded;
  }
}

export function getRuntimeConfig(): RuntimeConfig {
  return loaded;
}

function mergeWithDefaults(value: Partial<RuntimeConfig> | null | undefined): RuntimeConfig {
  const oauth: Partial<OAuthRuntimeConfig> = value?.oauth ?? {};
  return {
    oauth: {
      authority: typeof oauth.authority === 'string' ? oauth.authority : DEFAULT_CONFIG.oauth.authority,
      clientId: typeof oauth.clientId === 'string' && oauth.clientId.length > 0
        ? oauth.clientId
        : DEFAULT_CONFIG.oauth.clientId,
      scope: typeof oauth.scope === 'string' && oauth.scope.length > 0
        ? oauth.scope
        : DEFAULT_CONFIG.oauth.scope
    }
  };
}
