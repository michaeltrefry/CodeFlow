import { AuthConfig } from 'angular-oauth2-oidc';

export const authConfig: AuthConfig = {
  issuer: (window as unknown as { __cfAuthority?: string }).__cfAuthority ?? '',
  clientId: 'codeflow-ui',
  redirectUri: window.location.origin,
  responseType: 'code',
  scope: 'openid profile email',
  showDebugInformation: false,
  requireHttps: false
};
