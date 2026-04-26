# Keycloak setup for CodeFlow

CodeFlow authenticates against the existing Keycloak instance at `https://identity.trefry.net`. This document is the operator-facing checklist for the realm + client + role + protocol-mapper configuration the application expects. All of it is one-time setup against Keycloak; nothing in this repository changes when these are reconfigured.

If you have already done the equivalent setup for the Kanban Board app on the same Keycloak instance, the structure of this is identical — only the names change.

---

## Realm

Use the existing `trefry` realm: `https://identity.trefry.net/realms/trefry`.

The OIDC discovery endpoint is `https://identity.trefry.net/realms/trefry/.well-known/openid-configuration`. CodeFlow's API resolves the JWT issuer and signing keys from that document, so any change to the realm name MUST be mirrored in the production environment variables (Slice 6) — there is no Keycloak base URL stored in the repository.

---

## Clients

You need two clients in the `trefry` realm.

### `codeflow-ui` — public PKCE client

Used by the Angular frontend to drive the user-facing login. Configure:

| Field | Value |
|---|---|
| Client ID | `codeflow-ui` |
| Client type | OpenID Connect |
| Client authentication | **Off** (public client) |
| Authentication flow | Standard flow only — turn **off** Implicit, Direct grants, Service accounts |
| Root URL | `https://codeflow.trefry.net` |
| Home URL | `https://codeflow.trefry.net` |
| Valid redirect URIs | `https://codeflow.trefry.net/*` and `http://localhost:4200/*` |
| Valid post-logout redirect URIs | `https://codeflow.trefry.net/*` and `http://localhost:4200/*` |
| Web origins | `https://codeflow.trefry.net` and `http://localhost:4200` (so Keycloak emits CORS headers on its OIDC endpoints when called from those origins) |
| Proof Key for Code Exchange | See note below — required setting in older Keycloak; can be skipped in 26+ |

Front-channel logout is not currently used by CodeFlow; leave it disabled unless you wire it up later.

**Note on PKCE in Keycloak 26+**: The "Proof Key for Code Exchange Code Challenge Method" field and the underlying `pkce.code.challenge.method` attribute are not exposed in the standard Admin Console UI on Keycloak 26.6.1. This is fine — `codeflow-ui` uses `angular-oauth2-oidc` with `responseType: 'code'`, which always sends `code_challenge_method=S256` on the authorization request, and Keycloak honors PKCE on the verification side automatically. The attribute is a hardening knob that *forces* S256 (rejecting `plain` or absent challenges); it is not a prerequisite for PKCE to work. Verify in the browser network tab that the `…/protocol/openid-connect/auth?…` URL contains `code_challenge=…&code_challenge_method=S256` after first login (this is the Slice 8 check). If you need the attribute set on a Keycloak version that hides the UI field, set it via the kcadm CLI: `kcadm.sh update clients/<id> -r trefry -s 'attributes.pkce\.code\.challenge\.method=S256'`.

### `codeflow-api` — audience client

Used as the access-token `aud` value so the API can validate that an incoming token was actually intended for it. Configure:

| Field | Value |
|---|---|
| Client ID | `codeflow-api` |
| Client type | OpenID Connect |
| Client authentication | **On** (confidential — but the secret is unused in CodeFlow because the API does not call Keycloak admin APIs; the client only exists to be a target audience) |
| Authentication flow | All flows **off** (no direct end-user login through this client) |
| Service accounts roles | Off |

CodeFlow's `Auth__Audience` environment variable is set to `codeflow-api` to match this client ID.

---

## Protocol mappers (on `codeflow-ui` client OR on a client scope assigned to it)

The access tokens minted for `codeflow-ui` MUST carry these claims so CodeFlow can resolve the user identity and authorize requests.

**Where to find Mappers in Keycloak 26+**: Per-client mappers no longer live on a top-level "Mappers" tab. Instead, open `codeflow-ui` → **Client scopes** tab → click the **`codeflow-ui-dedicated`** scope row → then the **Mappers** sub-tab. That's where you add the two mappers below. (Alternatively, create a reusable realm-level Client scope named `codeflow` with the same mappers and assign it as a Default scope on `codeflow-ui` — same effect, slightly more reusable.)

### Audience mapper — `codeflow-api`

Adds `codeflow-api` to the JWT `aud` claim so the API accepts the token.

| Field | Value |
|---|---|
| Mapper type | **Audience** |
| Name | `codeflow-api-audience` |
| Included Custom Audience | `codeflow-api` |
| Add to access token | **On** |
| Add to ID token | Off |

### Roles mapper — `roles` claim

CodeFlow's `Auth__RolesClaim` defaults to `roles` and the role-based policy layer (`Auth/RoleBasedPermissionChecker`, `CodeFlowApiDefaults.PermissionRoleMatrix`) reads from there. Surface realm roles into that claim:

| Field | Value |
|---|---|
| Mapper type | **User Realm Role** |
| Name | `realm-roles-to-roles` |
| Token Claim Name | `roles` |
| Claim JSON Type | String |
| Multivalued | **On** |
| Add to access token | **On** |
| Add to ID token | Off |

If you prefer to gate CodeFlow access via a CLIENT role on `codeflow-api` instead of a REALM role, swap the mapper type to **User Client Role** and pick `codeflow-api` as the client.

### Standard subject / email / name claims

These usually come from the default `profile` and `email` client scopes. Confirm the access token contains:

- `sub` — the subject (CodeFlow's `Auth__SubjectClaim` defaults to `sub`).
- `email` (`Auth__EmailClaim`).
- `name` or `preferred_username` (`Auth__NameClaim` defaults to `name`).

If your realm's defaults emit `preferred_username` instead of `name`, set `Auth__NameClaim=preferred_username` in production rather than rewriting the mapper.

---

## Bootstrap roles

Create these realm roles in `trefry`. The names are case-sensitive; CodeFlow compares against lowercase constants in `CodeFlowApiDefaults.Roles`:

- `viewer`
- `author`
- `operator`
- `admin`

Assign at least one user to `admin` for the initial deploy. Other roles can be granted later as the team grows.

Permission matrix (defined in `CodeFlowApiDefaults.PermissionRoleMatrix`, summarized):

| Role | Has access to |
|---|---|
| `viewer` | Read-only access to agents, workflows, traces, MCP servers, agent roles, skills, git host, LLM providers |
| `author` | Viewer + write on agents, workflows, skills |
| `operator` | Viewer + write on traces, HITL decisions, ops/DLQ |
| `admin` | All of the above + write on MCP servers, agent roles, git host, LLM providers |

---

## Machine-to-machine clients (other apps calling CodeFlow's API)

If you want to call `/api/*` from another service (a CI bot, a sibling app, a scheduled job), do NOT reuse `codeflow-ui`. Create a dedicated **service-account client** per external app:

| Field | Value |
|---|---|
| Client ID | e.g. `codeflow-bot-jenkins`, `codeflow-bot-ingest`, etc. — one per caller |
| Client authentication | **On** (confidential) |
| Authorization | Off |
| Authentication flow | ✅ **Service accounts roles** only — turn off Standard flow, Direct grants, Implicit, OAuth 2.0 Device, OIDC CIBA |

Then on this client:

1. **Client scopes** tab → click `<client>-dedicated` → **Mappers** → **Add mapper → By configuration**:
   - **Audience** mapper exactly like the one on `codeflow-ui`: Included Client Audience = `codeflow-api`, Add to access token = ON. (Without this the API will 401 the bot's tokens.)
   - **User Realm Role** mapper if the bot needs roles: claim name `roles`, multivalued ON. Service accounts can have realm roles assigned.
2. **Service account roles** tab → click **Assign role** → pick whichever realm roles this bot needs (`viewer`, `author`, `operator`, `admin`). The CodeFlow permission matrix from earlier in this doc applies identically.
3. **Credentials** tab → copy the Client secret for the bot's deployment config.

The bot then mints tokens with the OAuth 2.0 client_credentials grant:

```bash
curl -s -X POST https://identity.trefry.net/realms/trefry/protocol/openid-connect/token \
  -d 'grant_type=client_credentials' \
  -d 'client_id=codeflow-bot-jenkins' \
  -d 'client_secret=<from Credentials tab>' \
  | jq -r .access_token
```

…and uses each token until it expires (typical 5–60 minutes; tunable on the client's Advanced tab → Access Token Lifespan). Tokens are JWT and can be inspected at jwt.io to confirm `aud` includes `codeflow-api` and `roles` matches what was assigned.

**Why per-app and not a shared service account?** Per-app clients give you a kill switch (revoke one client without affecting others), per-app role scoping (the ingest bot doesn't need admin), and per-app audit trail in Keycloak's events log.

## Verification checklist

After saving the above:

1. From the realm's account console (`https://identity.trefry.net/realms/trefry/account/`), log in as the seeded admin user and confirm you can see your assigned roles.
2. From `https://codeflow.trefry.net` (after Slice 5–6 deploy), an unauthenticated visit should redirect to `https://identity.trefry.net/realms/trefry/protocol/openid-connect/auth?client_id=codeflow-ui&...`.
3. After login, `GET https://codeflow.trefry.net/api/me` (in the browser dev tools network tab, with the bearer attached by the Angular interceptor) should return the user with the expected `roles` array.
4. A role-gated mutation (e.g. `POST /api/agents`) should succeed for `admin` and return 403 for `viewer`.

If `/api/me` returns 401 with `WWW-Authenticate: Bearer error="invalid_token"`, the most common causes are:

- Wrong `aud` — the token is missing `codeflow-api`. Re-check the audience mapper and that it's enabled in the access token.
- Wrong issuer — the API's `Auth__Authority` doesn't match the realm in the token's `iss`. They must match exactly, including trailing-slash behavior.
- Forwarded headers misconfigured — the API thinks the request came in over HTTP and rejects the metadata fetch when `Auth__RequireHttpsMetadata=true`. Slice 2 wires the forwarded-headers middleware; confirm Caddy is sending `X-Forwarded-Proto: https`.

---

## Where this doc fits

- The operator runs through this doc once per environment when standing up CodeFlow.
- The values it produces (realm name, audience, client IDs) become the GitHub Actions secrets/variables consumed by the deploy workflow (Slice 6) and surfaced into the runtime via `deploy/.env.release` (Slice 5).
- The end-to-end live verification happens in Slice 8 against the deployed `https://codeflow.trefry.net`.
