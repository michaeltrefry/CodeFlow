# sandbox-controller

Out-of-process sandbox controller for CodeFlow's agent-driven container jobs. See [`docs/sandbox-executor.md`](../docs/sandbox-executor.md) for the full design (epic 526, sc-527).

This directory holds the Go service that the CodeFlow API/Worker call when an agent invokes `run_container`. The controller is the only component allowed to talk to dockerd; the api/worker have no docker access. Sandbox jobs run under gVisor (`runsc`) for kernel-level isolation.

## Status

As of sc-529, `/run` actually spawns sibling containers under gVisor (`runsc`) with the locked-down defaults (read-only rootfs, `cap_drop ALL`, `no-new-privileges`, `network=none`, nonroot uid). Subsequent slices add the validator and the workspace plumbing:

| Slice | What it adds | Status |
|---|---|---|
| sc-528 | Service skeleton, mTLS, endpoint stubs, structured logging, distroless image | Done |
| sc-529 | gVisor (`runsc`) wiring — `/run` actually spawns sibling containers | Done |
| sc-538 | Controller container deploy hardening (AppArmor, seccomp, cap_drop, etc.) | Done |
| sc-530 | Image whitelist + policy store on the controller | Next |
| sc-531 | Workspace path validation + read-only mount + tmpfs scratch | Pending |
| sc-532 | CodeFlow side: `SandboxControllerRunner` + `ContainerTools:Backend` flag | Pending |
| sc-533 | Lifecycle + cleanup on the controller | Pending |
| sc-534 | OTLP traces + metrics + W3C trace propagation | Pending |
| sc-535 | Phase 1 deployment — sibling compose service in CodeFlow VM | Pending |
| sc-536 | Threat-model conformance tests | Pending |
| sc-537 | Production cutover + permanent DooD removal | Pending |
| sc-539 | Phase 2 graduation — separate executor VM + NFS | Backlog |

## Quick start (local dev)

Requires Go 1.26+ and openssl.

```bash
# Generate self-signed dev mTLS material in deploy/dev-tls/.
make dev-certs

# Drop a dev config in place (copy the example, paths point at deploy/dev-tls).
# Note the path + filename rewrites: prod uses /etc/cfsc-tls/ + ca.pem (per
# bootstrap-ca.sh + the un-nested compose mount); dev uses ./deploy/dev-tls/ +
# client-ca.pem (per gen-dev-certs.sh).
cp deploy/controller-config.example.toml deploy/controller-config.dev.toml
sed -i '' \
  -e 's|/etc/cfsc-tls/server.pem|deploy/dev-tls/server.pem|' \
  -e 's|/etc/cfsc-tls/server.key|deploy/dev-tls/server.key|' \
  -e 's|/etc/cfsc-tls/ca.pem|deploy/dev-tls/client-ca.pem|' \
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
│   ├── dockerd/                      # sc-529 — minimal hand-rolled HTTP client for the docker daemon
│   │   ├── client.go                 # ping, image pull, container create/start/wait/logs/kill/remove
│   │   ├── types.go                  # locked-down create payload (CapDrop ALL, ReadonlyRootfs, etc.)
│   │   ├── demux.go                  # docker logs multiplex demuxer (reimpl of moby/pkg/stdcopy)
│   │   └── client_test.go
│   ├── runner/                       # sc-529 — per-job orchestration (pull, create, run, capture, teardown)
│   │   ├── spec.go                   # JobSpec → CreateContainerRequest with security defaults
│   │   ├── runner.go                 # Run() with timeout/cancel paths
│   │   ├── stream.go                 # bounded buffer for stdout/stderr capture
│   │   ├── runner_test.go            # unit tests with stub Daemon
│   │   ├── stream_test.go
│   │   ├── spec_test.go
│   │   └── integration_test.go       # //go:build integration — real daemon + runsc required
│   └── server/
│       ├── server.go                 # Server type, Handler() mounting, LoadTLSConfig, JobRunner interface
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
