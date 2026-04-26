# Deploying CodeFlow to codeflow.trefry.net

This is the operator guide for the production deployment of CodeFlow on the Linode host. The pipeline lives in `.github/workflows/deploy.yml` (Slice 6) and the host-side Compose stack lives in `deploy/docker-compose.prod.yml` (Slice 5).

For Keycloak setup, see [docs/deployment-keycloak.md](deployment-keycloak.md).

---

## Architecture

```
                                Internet
                                    │
                            (HTTPS, port 443)
                                    │
              ┌─────────────────────▼─────────────────────┐
              │   Caddy (host-managed, NOT this repo)     │
              │   handles TLS, ACME, and reverse-proxy    │
              └─┬───────────────────────────────────────┬─┘
                │                                       │
   /api/* (incl. /api/traces/{id}/stream SSE)         everything else
                │                                       │
            127.0.0.1:5080                           127.0.0.1:4280
                │                                       │
        ┌───────▼────────┐                      ┌───────▼────────┐
        │  codeflow-api  │                      │  codeflow-ui   │
        │  (ASP.NET 10)  │                      │   (nginx)      │
        └─┬──────────────┘                      └────────────────┘
          │
          │  amqp           sql           amqp           sql
          ▼                ▼               ▼            ▼
  mqapps.trefry.net   mariadb        mqapps.trefry.net  mariadb
  (external RabbitMQ)  (on              (external)        (on
                       trefry-network)                    trefry-network)
                                ▲
                                │
                       ┌────────┴───────┐
                       │ codeflow-worker│
                       └────────────────┘
```

- TLS, ACME and HTTP→HTTPS redirect are owned by the host's Caddy. CodeFlow never speaks TLS itself.
- API and UI bind to `127.0.0.1` only — Caddy is the only public path in.
- `codeflow-worker` exposes nothing on the host.
- API and Worker join the external `trefry-network` Docker network so they can reach the shared `mariadb` container and (if the operator routes it through that network) `mqapps.trefry.net`.

---

## Server prerequisites

The Linode host must already have:

- Docker Engine + Docker Compose v2 (`docker compose` subcommand).
- A host-managed Caddy service handling TLS for `codeflow.trefry.net`.
- An external Docker network named `trefry-network`. The deploy workflow runs `docker network inspect trefry-network || docker network create trefry-network` so this is auto-created if missing, but the shared `mariadb` and any other apps that share the network must already be using it.
- A shared MariaDB container reachable as `mariadb` on `trefry-network`.
- Network reachability to `mqapps.trefry.net` (RabbitMQ + management) on whatever port the shared instance exposes.
- A deploy directory (default `/opt/codeflow`) and an artifacts directory (default `/opt/codeflow/artifacts`). The deploy step `mkdir -p`s them on first run.
- An SSH user with permission to run `docker` (typically a member of the `docker` group). The deploy workflow calls `docker compose ...` over SSH; no sudo prompt.

---

## One-time external setup

### Keycloak

Follow [docs/deployment-keycloak.md](deployment-keycloak.md) to create the `codeflow-ui` (public PKCE) and `codeflow-api` (audience) clients in the `trefry` realm at `https://identity.trefry.net`. Confirm the audience mapper, the `roles` claim mapper, and the four bootstrap roles (`viewer`/`author`/`operator`/`admin`).

### MariaDB

On the shared MariaDB instance:

```sql
CREATE DATABASE IF NOT EXISTS codeflow CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER IF NOT EXISTS 'codeflow'@'%' IDENTIFIED BY '<strong-random>';
GRANT ALL PRIVILEGES ON codeflow.* TO 'codeflow'@'%';
FLUSH PRIVILEGES;
```

The application applies EF Core migrations on startup; no separate migration step is required.

### RabbitMQ

On the shared RabbitMQ instance at `mqapps.trefry.net`:

```bash
rabbitmqctl add_vhost codeflow
rabbitmqctl add_user codeflow '<strong-random>'
rabbitmqctl set_permissions -p codeflow codeflow '.*' '.*' '.*'
# Optional: tag the user for the management UI if you want to inspect the vhost there.
rabbitmqctl set_user_tags codeflow management
```

### Caddy snippet

Add this site block to your host's Caddyfile (the host owns this — it is intentionally NOT in the CodeFlow repo so multiple apps' Caddyfiles can be managed centrally):

```caddyfile
codeflow.trefry.net {
    encode zstd gzip

    # API (including the trace SSE stream at /api/traces/{id}/stream).
    handle /api/* {
        reverse_proxy 127.0.0.1:5080 {
            # Disable response buffering so SSE keeps streaming.
            flush_interval -1
            transport http {
                read_timeout 1h
            }
        }
    }

    # Anything else: the static Angular shell from nginx.
    handle {
        reverse_proxy 127.0.0.1:4280
    }
}
```

Caddy automatically sets `X-Forwarded-Proto`, `X-Forwarded-Host`, and `X-Forwarded-For` on its `reverse_proxy` directive. The CodeFlow API's forwarded-headers middleware (Slice 2) trusts those without further configuration because the API binds to `127.0.0.1` only.

After editing, reload Caddy: `caddy reload --config /etc/caddy/Caddyfile`.

### Optional: expose the API on a dedicated subdomain for M2M clients

If other apps need to call CodeFlow's API server-to-server (CI bots, sibling services, scheduled jobs), it's cleaner to give the API its own subdomain so the URLs are unambiguous and you can rate-limit / monitor it independently:

```caddyfile
codeflow-api.trefry.net {
    reverse_proxy 127.0.0.1:5080 {
        flush_interval -1
        transport http {
            read_timeout 1h
        }
    }
}
```

The exposed surface is fully OAuth-protected — Slice 1's startup guard refuses to start the API in production unless `Auth__Authority` and `Auth__Audience` are set, and every endpoint requires an authenticated principal. External callers authenticate with Keycloak service-account tokens (see [docs/deployment-keycloak.md](deployment-keycloak.md#machine-to-machine-clients-other-apps-calling-codeflows-api)).

CORS does NOT need to be widened for server-to-server callers (CORS is a browser-only mechanism). Only add origins to `Cors__AllowedOrigins__N` if external **browser** SPAs need to call the API cross-origin.

---

## GitHub repository configuration

### Required secrets

Set these as **Repository secrets** (Settings → Secrets and variables → Actions → New repository secret), or as **Environment secrets** scoped to a `production` Environment:

| Secret | Purpose |
|---|---|
| `LINODE_SSH_KEY` *or* `LINODE_SSH_KEY_B64` | SSH private key for the deploy user. Use `_B64` if the raw multiline form gives you trouble in the secret editor (`base64 -w0 < ~/.ssh/id_codeflow_deploy`). |
| `LINODE_SSH_KEY_PASSPHRASE` | Optional. Only set if the key has a passphrase. |
| `CODEFLOW_DB_CONNECTION_STRING` | `Server=mariadb;Port=3306;Database=codeflow;User=codeflow;Password=<…>;` |
| `CODEFLOW_RABBITMQ_USERNAME` | RabbitMQ service user (e.g. `codeflow`). |
| `CODEFLOW_RABBITMQ_PASSWORD` | RabbitMQ password. |
| `CODEFLOW_SECRETS_MASTER_KEY` | 32-byte AES-GCM master key, base64. Generate with `openssl rand -base64 32`. |
| `CODEFLOW_OPENAI_API_KEY` | Optional — leave unset to disable the OpenAI provider. |
| `CODEFLOW_ANTHROPIC_API_KEY` | Optional — leave unset to disable the Anthropic provider. |

### Variables (or secrets — your choice)

Non-sensitive values may be set as **Repository variables** OR as secrets. The workflow accepts either source via `vars.NAME || secrets.NAME`:

| Name | Default | Purpose |
|---|---|---|
| `LINODE_HOST` | (required) | Hostname or IP of the Linode VM. |
| `LINODE_USER` | (required) | SSH user (member of `docker` group). |
| `LINODE_SSH_PORT` | `22` | Override if SSH is on a non-standard port. |
| `CODEFLOW_DEPLOY_DIR` | `/opt/codeflow` | Directory on the host where compose + .env.release live. |
| `CODEFLOW_AUTH_AUTHORITY` | (required) | Keycloak realm issuer, e.g. `https://identity.trefry.net/realms/trefry`. |
| `CODEFLOW_AUTH_AUDIENCE` | (required) | JWT `aud`, e.g. `codeflow-api`. |
| `CODEFLOW_OAUTH_CLIENT_ID` | (required) | Public OIDC client id, e.g. `codeflow-ui`. |
| `CODEFLOW_OAUTH_SCOPE` | `openid profile email` | Adjust if you add custom scopes. |
| `CODEFLOW_PUBLIC_ORIGIN` | `https://codeflow.trefry.net` | Sets the API CORS allow-list and is informational in `.env.release`. |
| `CODEFLOW_RABBITMQ_HOST` | `mqapps.trefry.net` | Override only if you move the bus. |
| `CODEFLOW_RABBITMQ_PORT` | `5672` | Override for AMQPS or non-standard ports. |
| `CODEFLOW_RABBITMQ_MANAGEMENT_PORT` | `15672` | RabbitMQ HTTP management port (used by the dead-letter helper). |
| `CODEFLOW_RABBITMQ_VHOST` | (required) | RabbitMQ vhost (e.g. `codeflow`). |
| `CODEFLOW_API_HOST_PORT` | `5080` | Loopback port the API listens on for Caddy. |
| `CODEFLOW_UI_HOST_PORT` | `4280` | Loopback port the UI listens on for Caddy. |
| `CODEFLOW_ARTIFACTS_DIR` | `/opt/codeflow/artifacts` | Host bind mount for artifacts. |
| `CODEFLOW_OTLP_ENDPOINT` | empty | Optional OTLP sink for observability. |

The deploy job's preflight collects every missing required value into one grouped error before failing, so on first setup you'll see the entire list of what still needs to be set, not one secret at a time.

---

## First deploy

1. Complete Keycloak, MariaDB, RabbitMQ, and Caddy setup above.
2. Set the GitHub secrets and variables.
3. Trigger the workflow:
   - Either push to `main`, or
   - Run `Build, push, and deploy CodeFlow` manually from the **Actions** tab via `workflow_dispatch`.
4. Watch the run. The `test` and `images` jobs should pass on green; the `deploy` job will preflight, SSH to the host, write `/opt/codeflow/.env.release`, ensure `trefry-network`, then `docker compose ... pull && up -d`.
5. Open `https://codeflow.trefry.net` — you should be redirected to Keycloak. Log in as a user assigned the `admin` role.

---

## Routine updates

Push a commit to `main`. The workflow:

1. Builds + tests on a fresh runner.
2. Pushes new GHCR images tagged `sha-<short>` and `latest`.
3. SSHes to the host, regenerates `.env.release` (image refs include the new SHA), ensures the network, pulls the new images, and `up -d` reconciles the running containers.

The deploy is idempotent. Re-running on the same SHA is a no-op apart from a `docker compose pull`.

---

## Rollback

To roll back to a previous good commit:

1. From the **Actions** tab pick a previous green run of `Build, push, and deploy CodeFlow`.
2. Note its commit SHA.
3. Click **Run workflow** and target that SHA's branch (or temporarily revert `main` to it).
4. The deploy job will rewrite `.env.release` with `sha-<short>` of that commit and reconcile the stack to those images.

Alternatively, on the host directly: edit `/opt/codeflow/.env.release` to pin `CODEFLOW_API_IMAGE` / `CODEFLOW_WORKER_IMAGE` / `CODEFLOW_UI_IMAGE` to a known-good `sha-<short>` tag and re-run `docker compose --env-file .env.release -f deploy/docker-compose.prod.yml up -d`. The next deploy from `main` will overwrite this.

---

## Troubleshooting

### `https://codeflow.trefry.net` returns 401 on every API call

- Most common cause: token `aud` doesn't include `codeflow-api`. Re-check the **Audience** mapper in Keycloak (see `docs/deployment-keycloak.md`), then log out and back in to mint a fresh token.
- Check `Auth__Authority` matches Keycloak's `iss` exactly (including trailing-slash behavior).
- Confirm `Auth__RequireHttpsMetadata=true` and the API can reach `https://identity.trefry.net/...` — Keycloak metadata fetch failures look like 401 to the caller.

### OIDC login redirects to `http://codeflow.trefry.net/...` instead of `https://...`

The forwarded-headers middleware (Slice 2) is not seeing `X-Forwarded-Proto: https`. Confirm:

- Caddy is reverse-proxying via the `reverse_proxy` directive (which sets the headers automatically).
- `app.UseForwardedHeaders()` runs before `app.UseAuthentication()` in `CodeFlow.Api/Program.cs` (already wired in Slice 2).

### `502 Bad Gateway` from Caddy

- The API container isn't bound to the loopback port Caddy is calling. Check `CODEFLOW_API_HOST_PORT` in `.env.release` matches the port in the Caddy snippet.
- `docker compose ps` on the host: API container should be `Up` and listening on `127.0.0.1:5080->8080/tcp`.

### `Auth:DevelopmentBypass is disabled but the following required OIDC settings are empty`

The Slice 1 fail-fast caught the production startup with missing `Auth__Authority` or `Auth__Audience`. Inspect `/opt/codeflow/.env.release` — both values must be set. The deploy preflight should have caught this; if it didn't, the secrets/variables aren't where the workflow expects them.

### RabbitMQ connectivity errors in the API/Worker logs

- DNS: `docker exec codeflow-api getent hosts mqapps.trefry.net` should resolve.
- Credentials: confirm the vhost / username / password set on the shared RabbitMQ matches the secrets.
- If you're using TLS / AMQPS on a non-standard port, set `CODEFLOW_RABBITMQ_PORT` accordingly. The current Compose file uses plain AMQP on `5672`.

### EF Core migration failures on startup

- Connection string: `docker exec codeflow-api env | grep CODEFLOW_DB_CONNECTION_STRING` and confirm the format matches `Server=mariadb;Port=3306;Database=codeflow;User=...;Password=...;`.
- Permissions: the `codeflow` MySQL user needs `ALL PRIVILEGES` on the `codeflow` database (`SHOW GRANTS FOR 'codeflow'@'%';`).
- If a migration partially applied, use the MariaDB client to inspect `__EFMigrationsHistory` and roll back by hand. There is no automated rollback for EF Core migrations.

### "trefry-network: network not found"

The deploy step ensures it, but if you're running compose by hand on the host:

```bash
docker network inspect trefry-network >/dev/null 2>&1 || docker network create trefry-network
```

---

## Related docs

- [docs/deployment-keycloak.md](deployment-keycloak.md) — Keycloak realm + client configuration.
- [docs/local-integration-stack.md](local-integration-stack.md) — local development docker-compose stack.
