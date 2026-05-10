# Image whitelist updates (sc-535)

The controller's allowed-images list is enforced by the layer-2 validator (sc-530, default-deny).

## Where to edit

The canonical source is in the repo at [`sandbox-controller/deploy/controller-config.toml`](../../sandbox-controller/deploy/controller-config.toml). The deploy workflow scps that file to `/opt/codeflow/cfsc/config/config.toml` on every release, so any hand-edit on the host is overwritten on the next deploy.

**Standard flow (preferred):** open a PR adding entries to the in-repo file; CI validates the file parses + passes `config.validate()`; merge to `main` re-deploys. The controller restarts as part of the deploy and picks the new allowlist up immediately.

**Hot-fix flow (use sparingly):** if you need to widen the allowlist without waiting for a deploy — e.g. to unblock an in-flight job — edit `/opt/codeflow/cfsc/config/config.toml` on the host directly and SIGHUP the controller. This is hot-reloadable (TLS / listen address are NOT). **Land the same change in the repo immediately afterward** so the next deploy doesn't roll back your edit.

The relevant section:

```toml
# Each block is one rule. Tag patterns: exact, trailing wildcard ("10.0-*"),
# catch-all "*", or "" (defaults to "latest"). No regex, no internal wildcards.

[[images.allowed]]
registry   = "ghcr.io"
repository = "trefry/dotnet-tester"
tag        = "10.0-sdk-*"

[[images.allowed]]
registry   = "docker.io"
repository = "library/alpine"
tag        = "3"
```

## Standard procedure (in-repo edit + redeploy)

```bash
# 1. Edit the canonical file in the repo.
$EDITOR sandbox-controller/deploy/controller-config.toml

# 2. Validate locally before pushing (the same test CI runs).
(cd sandbox-controller && go test ./internal/config/...)

# 3. Open a PR; merge to main re-deploys.
```

The deploy workflow ships the new file and `docker compose up -d` restarts the controller, which reads the updated allowlist on startup.

## Hot-fix procedure (host SIGHUP)

Use only when you can't wait for a deploy. Land the matching repo PR immediately after.

```bash
# 1. Edit the deployed file on the host (will be overwritten on next deploy).
sudo "${EDITOR:-vi}" /opt/codeflow/cfsc/config/config.toml

# 2. Send SIGHUP to the running controller. On parse failure, the controller
#    keeps the previous allowlist and logs an error; the running process is
#    never left half-loaded.
docker kill --signal=HUP codeflow-sandbox-controller

# 3. Verify the reload was picked up. The /version endpoint surfaces a
#    SHA-256 hash of the raw config file — it changes only when the file
#    actually does. Use the deploy ops client cert to query.
curl --cacert /opt/codeflow/cfsc-client/api/server-ca.pem \
     --cert   /opt/codeflow/cfsc-client/api/client.pem \
     --key    /opt/codeflow/cfsc-client/api/client.key \
     --resolve codeflow-sandbox-controller:8443:127.0.0.1 \
     https://codeflow-sandbox-controller:8443/version

# Expect: {"commit":"sha-...","buildTime":"...","configHash":"<new sha>"}

# 4. Mirror the edit in the repo and ship a PR so the next deploy doesn't
#    overwrite the hot-fix.
```

The controller logs:

```
{"level":"INFO","msg":"SIGHUP received; reloading config","path":"/etc/cfsc/config.toml"}
{"level":"INFO","msg":"config reloaded","config_hash":"<new sha>","allowed_images":N}
```

## What SIGHUP does NOT reload

- TLS material (server cert, client CA bundle, client subject allowlist) — these are loaded into the listener at process start. Restart the process for those.
- The listen address.
- The docker socket path.
- The runner timeouts.

Restart the controller for any of those changes.

## Rollback

If you mis-edit the file, the SIGHUP reload **keeps the previous allowlist** and logs an error:

```
{"level":"ERROR","msg":"config reload failed; keeping previous","err":"…"}
```

So the running controller is never left in a half-loaded state. Fix the file and SIGHUP again.

## Audit trail

The standard flow already gives you an audit trail for free: the in-repo edit + PR captures rationale + reviewer + timestamp, and the deploy workflow's run is the link between the merged commit and the running config.

For hot-fixes, capture the new `configHash` from `/version` in the incident record, then ship the matching PR. The hash is the single source of truth for "what config is actually running" — confirm it matches the merged commit's file once the follow-up deploy lands.
