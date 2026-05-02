# Image whitelist updates (sc-535)

The controller's allowed-images list is enforced by the layer-2 validator (sc-530, default-deny). Updating it is a config-edit + SIGHUP — no restart required.

## File location

```
/opt/codeflow/cfsc/config/config.toml
```

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

## Procedure

```bash
# 1. Edit the config.
sudo "${EDITOR:-vi}" /opt/codeflow/cfsc/config/config.toml

# 2. Validate that the file still parses (the controller does this on
#    SIGHUP and rolls back on parse failure, but it's better to catch
#    typos before applying them).
docker run --rm -v /opt/codeflow/cfsc/config:/cfg:ro \
  ghcr.io/michaeltrefry/codeflow-sandbox-controller:latest \
  /usr/local/bin/sandbox-controller -config /cfg/config.toml -version-only

# 3. Send SIGHUP to the running controller.
docker kill --signal=HUP codeflow-sandbox-controller

# 4. Verify the reload was picked up. The /version endpoint surfaces a
#    SHA-256 hash of the raw config file — it changes only when the file
#    actually does. Use the deploy ops client cert to query.
curl --cacert /opt/codeflow/cfsc-client/api/server-ca.pem \
     --cert   /opt/codeflow/cfsc-client/api/client.pem \
     --key    /opt/codeflow/cfsc-client/api/client.key \
     --resolve codeflow-sandbox-controller:8443:127.0.0.1 \
     https://codeflow-sandbox-controller:8443/version

# Expect: {"commit":"sha-...","buildTime":"...","configHash":"<new sha>"}
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

Every image-whitelist update should land in version control alongside a short rationale. Suggested workflow:

1. Open a change in the config repo (or the `deploy/sandbox-controller/` subtree if you keep it there).
2. Get a second pair of eyes on the rule before applying.
3. Apply on the host, capture the new `configHash` from `/version` in the change record.
4. The hash is the single source of truth for "what config is actually running" — confirm it matches what you reviewed before declaring the change applied.
