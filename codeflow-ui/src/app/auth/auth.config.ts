import { AuthConfig } from 'angular-oauth2-oidc';
import { getRuntimeConfig } from '../core/runtime-config';

/**
 * Build the OIDC client config from the runtime-config.json that was fetched before bootstrap.
 * Call this AFTER `loadRuntimeConfig()` has resolved (handled in main.ts) so the same UI bundle
 * can serve any environment without a rebuild.
 */
export function buildAuthConfig(): AuthConfig {
  const runtime = getRuntimeConfig();
  return {
    issuer: runtime.oauth.authority,
    clientId: runtime.oauth.clientId,
    redirectUri: window.location.origin,
    responseType: 'code',
    scope: runtime.oauth.scope,
    showDebugInformation: false,
    requireHttps: false
  };
}

/** True when an OIDC issuer was configured for this environment. */
export function hasAuthConfigured(): boolean {
  return getRuntimeConfig().oauth.authority.length > 0;
}
