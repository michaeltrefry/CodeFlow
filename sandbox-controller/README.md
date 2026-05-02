# sandbox-controller

Out-of-process sandbox controller for CodeFlow's agent-driven container jobs. See [`docs/sandbox-executor.md`](../docs/sandbox-executor.md) for the full design (epic 526, sc-527).

This directory holds the Go service that the CodeFlow API/Worker call when an agent invokes `run_container`. The controller is the only component allowed to talk to dockerd; the api/worker have no docker access. Sandbox jobs run under gVisor (`runsc`) for kernel-level isolation.

## Status

This is the sc-528 scaffold. The service exposes the four endpoints with mTLS-only authentication and unknown-field-strict JSON decoding, but `/run` echoes the request rather than spawning a container. Subsequent slices add real behaviour:

| Slice | What it adds |
|---|---|
| sc-528 (this) | Service skeleton, mTLS, endpoint stubs, structured logging, distroless image |
| sc-529 | gVisor (`runsc`) wiring — `/run` actually spawns sibling containers |
| sc-530 | Image whitelist + policy store on the controller |
| sc-531 | Workspace path validation + read-only mount + tmpfs scratch |
| sc-532 | CodeFlow side: `SandboxControllerRunner` + `ContainerTools:Backend` flag |
| sc-533 | Lifecycle + cleanup on the controller |
| sc-534 | OTLP traces + metrics + W3C trace propagation |
| sc-535 | Phase 1 deployment — sibling compose service in CodeFlow VM |
| sc-536 | Threat-model conformance tests |
| sc-537 | Production cutover + permanent DooD removal |
| sc-538 | Controller container deploy hardening (AppArmor, seccomp, cap_drop, etc.) |
| sc-539 | Phase 2 graduation — separate executor VM + NFS |

## Quick start (local dev)

Requires Go 1.26+ and openssl.

```bash
# Generate self-signed dev mTLS material in deploy/dev-tls/.
make dev-certs

# Drop a dev config in place (copy the example, paths point at deploy/dev-tls).
cp deploy/controller-config.example.toml deploy/controller-config.dev.toml
sed -i '' \
  -e 's|/etc/cfsc/tls/server.pem|deploy/dev-tls/server.pem|' \
  -e 's|/etc/cfsc/tls/server.key|deploy/dev-tls/server.key|' \
  -e 's|/etc/cfsc/tls/client-ca.pem|deploy/dev-tls/client-ca.pem|' \
  deploy/controller-config.dev.toml

# Build and run.
make run
```

Probe `/healthz` from another shell:

```bash
curl --cacert deploy/dev-tls/client-ca.pem \
     --cert   deploy/dev-tls/codeflow-api.pem \
     --key    deploy/dev-tls/codeflow-api.key \
     --resolve codeflow-sandbox-controller:8443:127.0.0.1 \
     https://codeflow-sandbox-controller:8443/healthz
```

A request without `--cert/--key` fails the TLS handshake — that is the intended behaviour.

## Layout

```
sandbox-controller/
├── cmd/sandbox-controller/main.go    # entrypoint — flag parsing, config load, server lifecycle
├── internal/
│   ├── apilog/log.go                 # slog JSON logger
│   ├── auth/mtls.go                  # post-handshake subject allowlist verifier
│   ├── auth/mtls_test.go
│   ├── config/config.go              # TOML loader + validation + raw-file SHA256 hash for /version
│   └── server/
│       ├── server.go                 # Server type, Handler() mounting, LoadTLSConfig
│       ├── handlers.go               # POST /run, POST /cancel, GET /healthz, GET /version
│       ├── handlers_test.go          # mTLS + endpoint integration tests with self-signed PKI
│       └── middleware.go             # subject-allowlist + structured-logging middleware
├── deploy/
│   ├── Dockerfile                    # static Go build → distroless/static:nonroot
│   ├── compose.yml                   # local-dev compose (hardened posture, dev carve-outs called out)
│   ├── compose.prod.snippet.yml      # prod-shaped service definition for sc-535 to integrate
│   ├── controller-config.example.toml
│   ├── HARDENING.md                  # sc-538 — lever-to-threat mapping + verification checklist
│   ├── apparmor/codeflow-sandbox-controller   # AppArmor profile (host-installed)
│   └── seccomp/controller-seccomp.json        # seccomp profile (deny-by-default allowlist)
├── scripts/gen-dev-certs.sh          # openssl-based self-signed PKI for local dev
├── go.mod / go.sum
└── Makefile
```

## API

See [`docs/sandbox-executor.md` §11](../docs/sandbox-executor.md) for the full schemas. Summary:

- `POST /run` — submit a job (sc-528 echoes; sc-529+ runs).
- `POST /cancel` — cancel by `jobId`.
- `GET /healthz` — liveness, **mTLS-required** like everything else.
- `GET /version` — build commit, build time, config-file hash.

All non-2xx responses use the error envelope:

```json
{
  "error": {
    "code": "request_invalid",
    "message": "...",
    "rule": "scaffold_shape",
    "jobId": "..."
  }
}
```

`code` is from a fixed enum so callers map codes to retry semantics deterministically. The current scaffold codes are `request_invalid`, `mtls_required`, `mtls_subject_not_allowed`. Later slices add `image_not_allowed`, `workspace_invalid`, `limits_exceeded`, `forbidden_field`, `daemon_error`, etc.

## Tests

```bash
make test
```

Tests stand up an httptest TLS server with a self-contained PKI (CA + server cert + per-test client certs), exercising:

- TLS handshake fails without a client cert (mTLS required).
- A client cert with an allowlisted subject is accepted on `/healthz` and `/version`.
- A client cert with a non-allowlisted subject is rejected with `mtls_subject_not_allowed`.
- `/run` echoes a valid request and rejects unknown fields, missing fields, non-UUID jobIds, and non-positive timeouts.
- `/cancel` accepts a UUID jobId (returns 204) and rejects non-UUIDs.

Subject-allowlist matching itself is unit-tested in `internal/auth`.

## Why mTLS even in dev?

The slice description for sc-528 was deliberate: the controller has no environment in which it accepts plaintext or unauthenticated traffic. Local development uses self-signed certs (the `make dev-certs` flow), but the auth posture is identical to production. The reason: every "I'll just turn it off in dev" knob is one knob away from being in production by accident.
