# Sandbox controller — incident response (sc-535)

Phase-1 reality: the controller shares a VM with the rest of the CodeFlow stack. A controller compromise is therefore a **CodeFlow-VM-compromise** — the daemon socket the controller has access to is on the same host as the api, the worker, the UI, the assistant workspace, and `.env.release`. Treat the whole VM as suspect.

Phase 2 (sc-539) eliminates this — when the controller lives on a separate executor VM, its compromise is contained to that VM and CodeFlow's data, secrets, and public traffic are unaffected.

## Indicators

- Audit alerts from AppArmor / seccomp (DENIED entries in journalctl mentioning `codeflow-sandbox-controller`).
- Sweep service log lines about containers that the controller didn't expect to exist.
- Containers spawned with labels that don't match the controller's stamping pattern (`cf-managed=true`, `cf.traceId=…`, `cf.jobId=…`).
- OTLP metrics surge: `cfsc.validator.rejections` spike with rules you don't recognise, or `cfsc.jobs.failed` with no corresponding agent activity.
- Anyone reporting a CVE that affects the controller's dep tree (Go runtime, OTel SDK, `BurntSushi/toml`, `google/uuid`) — re-deploy is the right move regardless of incident.

## Phase 1 response

The controller-on-CodeFlow-VM scenario.

### 1. Isolate

```bash
# As root, on the affected VM.
docker stop codeflow-sandbox-controller codeflow-api codeflow-worker codeflow-ui

# Keep the VM running for forensics — do NOT destroy until you've captured what you need.
# Cut the network at the firewall layer (cloud provider's instance ACL or local iptables).
```

### 2. Preserve evidence

```bash
# Container exit logs.
docker logs codeflow-sandbox-controller > /tmp/cfsc.last.log
docker logs codeflow-api               > /tmp/api.last.log
docker logs codeflow-worker            > /tmp/worker.last.log

# Daemon-side audit.
journalctl -u docker --since "24 hours ago" > /tmp/dockerd.log

# Workdir snapshots — what jobs ran, what's left in .results/.
sudo tar -C /opt/codeflow -cf /tmp/codeflow-state.tar workdirs cfsc cfsc-client

# AppArmor / seccomp DENIED entries.
journalctl --since "24 hours ago" | grep -E 'apparmor|audit|SECCOMP' > /tmp/security.log
```

Get those off the VM before doing anything destructive (scp to ops laptop or block storage).

### 3. Rotate every secret

Treat **every** value in `.env.release` as compromised:

- `CODEFLOW_DB_CONNECTION_STRING` → rotate the DB credentials at MariaDB.
- `CODEFLOW_RABBITMQ_PASSWORD` → rotate the queue user.
- `CODEFLOW_SECRETS_MASTER_KEY` → rotate AND re-encrypt the secrets store on the new host. (This breaks any in-flight encrypted state — capture before destroying the VM.)
- `CODEFLOW_OPENAI_API_KEY`, `CODEFLOW_ANTHROPIC_API_KEY` → rotate at the provider.
- OAuth client secret at Keycloak → rotate.
- mTLS CA + leaves → re-issue from a clean ops machine per [`cert-rotation.md`](cert-rotation.md).

### 4. Rebuild

Phase-1 best practice is to **destroy the VM** and provision a fresh one rather than attempt remediation in-place. The attacker may have left persistence we won't catch by listing files. From the new VM:

```bash
# Standard host setup again.
./deploy/sandbox-controller/scripts/bootstrap-ca.sh /opt/codeflow/cfsc
# … rest per host-setup.md
```

Re-deploy from a clean GHA run (the deploy workflow re-pushes images and writes a fresh `.env.release`).

### 5. Postmortem inputs

- Audit log: which container labels existed pre-compromise that don't match the controller's stamping pattern? (Indicates the attacker got past the validator.)
- Metric history: when did `cfsc.validator.rejections{rule=…}` spike? What rule?
- Dep tree: was a dependency upgrade in flight that introduced this? (Check `go.sum` history.)
- AppArmor / seccomp DENIED entries: which syscalls / paths did the attacker try? (Validates the hardening levers were doing their job until they weren't.)

## Phase 2 response (sc-539, future)

Same shape, but the blast radius is the executor VM only. The CodeFlow VM stays up, the api/worker keep serving, you isolate and rebuild the executor while users see degraded `run_container` behaviour but otherwise normal service. The dual-trust mTLS-CA bridge described in [`cert-rotation.md`](cert-rotation.md) lets you bring up a new executor without flipping the api/worker `ServerCAPath` until the new executor proves stable.

## Gathering before-the-incident hardening evidence

Worth doing in advance: capture the verification output from [`HARDENING.md`](../sandbox-controller/deploy/HARDENING.md) periodically (cron, weekly) and ship it to your audit log. After an incident, you'll want to know whether the levers were intact at the time. Suggested fields to capture per cycle:

- `docker exec codeflow-sandbox-controller cat /proc/self/status` — CapEff / NoNewPrivs.
- `aa-status | grep codeflow` — AppArmor mode (enforce vs complain).
- `cat /proc/$(docker inspect …)/status | grep Seccomp` — Seccomp filter active (mode 2).
- `docker network inspect controller-internal` — Internal: true.
- `docker port codeflow-sandbox-controller` — empty.
- The image SHA running, and its build provenance.

If any of these change unexpectedly, that's a signal worth investigating.
