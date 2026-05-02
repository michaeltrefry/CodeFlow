# Sandbox controller — threat-model conformance tests (sc-536)

These probes assert the security claims in [`docs/sandbox-executor.md`](../../../docs/sandbox-executor.md) §4–§10 actually hold against a running controller. Each script targets one rule in the threat model; together they form the operator's "the levers are still in effect" verification.

Run them:

- After every fresh deploy ([host-setup.md](../host-setup.md)) — confirms the new build still rejects what it should.
- Before every release cut — regression guard.
- Periodically (weekly suggested) — drift detection.
- Whenever the AppArmor / seccomp / image-whitelist / TLS config changes — direct verification of the change.

Each script exits 0 on pass, non-zero on fail, and emits a single-line summary so they're easy to wire into a cron-and-archive workflow.

## Prereqs

- The full stack is up: \`docker compose ps\` shows \`codeflow-sandbox-controller\` healthy.
- mTLS material lives at \`/opt/codeflow/cfsc-client/api/{client.pem, client.key, server-ca.pem}\` (operator-deploy already satisfies this).
- \`curl\` 7.81+, \`jq\`, \`python3\`, and the host has docker available for the dockerd-side probes.
- The probes target the controller via \`https://codeflow-sandbox-controller:8443\` — run them from a host that resolves that name (the CodeFlow VM does, via the \`controller-internal\` compose network). On a different host, set \`CFSC_URL\` to the right base URL.

## Usage

```bash
cd /opt/codeflow/deploy/sandbox-controller/conformance
./run-all.sh
```

Per-probe:

```bash
./01-mtls-required.sh
./02-image-not-allowed.sh
./03-workspace-invalid.sh
./04-forbidden-field-rejected.sh
./05-sandbox-no-network-egress.sh
./06-gvisor-active.sh
./07-memory-cap-enforced.sh
./08-pids-cap-enforced.sh
./09-cleanup-correctness.sh
```

## What each probe asserts (mapping to threat-model rules)

| # | Probe | Rule from doc |
|---|---|---|
| 01 | `mtls-required` | §11.5: TLS 1.3, RequireAndVerifyClientCert, no plaintext path |
| 02 | `image-not-allowed` | §10.2: default-deny image policy on the controller side |
| 03 | `workspace-invalid` | §10.2 + §9.3: path traversal, symlink escape, blank segments |
| 04 | `forbidden-field-rejected` | §10.2: strict JSON decode rejects unknown fields like `privileged:true` |
| 05 | `sandbox-no-network-egress` | §6.4: sandbox containers run with `--network=none` |
| 06 | `gvisor-active` | §6.4: `--runtime=runsc` is registered and used |
| 07 | `memory-cap-enforced` | §6.4: per-job memory limit; OOM-killer fires |
| 08 | `pids-cap-enforced` | §6.4: per-job pids limit |
| 09 | `cleanup-correctness` | sc-529 + sc-533: per-job container/scratch teardown after exit |

## What these probes do NOT cover

- Controller-process compromise (Phase-1 residual risk that sc-539 / Phase 2 closes — by design these probes assume the controller binary is intact).
- Kernel-level CVEs that escape gVisor (out of scope; same risk as any docker setup).
- mTLS CA root compromise (covered by `cert-rotation.md` runbook).
- Denial of service via legitimate-looking but flooded `/run` calls (capacity / rate-limit story is a separate slice).

## Adding new probes

Each probe is a standalone bash script that:

1. Sources `_lib.sh` for the curl helpers + assertion macros.
2. Runs its scenario.
3. Prints `PASS: <one-line summary>` on success or `FAIL: <reason>` on failure.
4. Exits with the appropriate status.

Keep them small (≤ 50 lines each, including comments). If a scenario needs more setup, factor the helpers into `_lib.sh` rather than expanding any single probe.

A new probe lands with a row in the table above. The slice tracking the new check should reference this directory.
