# Sandbox controller — host setup runbook (sc-535)

This document walks through bringing a fresh Iron Mountain VM up to the point where `docker compose up codeflow-sandbox-controller` will succeed. The application-layer artifacts (the Go binary, AppArmor / seccomp profiles, prod compose snippet) come from the [`sandbox-controller/`](../../sandbox-controller) subtree — you don't author them by hand here, you just install them on the host.

Phase 1 puts the controller on the **same VM** as the rest of the CodeFlow stack. Phase 2 (sc-539) graduates to a separate VM; the steps below are equally valid for that future host with a single tweak (run them on the executor VM only).

## Prereqs on the VM

The IM provisioning provides:

- Ubuntu 22.04+ or any kernel ≥ 5.15.
- Docker Engine 26+ (the controller's `dockerd` API client uses `/v1.45`).
- AppArmor enabled (`aa-status` succeeds and shows the kernel module loaded).
- A public IP fronted by Caddy with TLS (per the existing CodeFlow deploy).

## 1. Install gVisor (`runsc`)

```bash
# As root.
ARCH="$(uname -m)"
URL="https://storage.googleapis.com/gvisor/releases/release/latest/${ARCH}"

curl -fsSL -o /tmp/runsc       "${URL}/runsc"
curl -fsSL -o /tmp/runsc.sha512 "${URL}/runsc.sha512"
curl -fsSL -o /tmp/containerd-shim-runsc-v1       "${URL}/containerd-shim-runsc-v1"
curl -fsSL -o /tmp/containerd-shim-runsc-v1.sha512 "${URL}/containerd-shim-runsc-v1.sha512"

(cd /tmp && sha512sum -c runsc.sha512 containerd-shim-runsc-v1.sha512)

install -m 0755 /tmp/runsc /usr/local/bin/
install -m 0755 /tmp/containerd-shim-runsc-v1 /usr/local/bin/
```

Register `runsc` as a docker runtime:

```bash
mkdir -p /etc/docker
cat > /etc/docker/daemon.json <<'JSON'
{
  "runtimes": {
    "runsc": {
      "path": "/usr/local/bin/runsc"
    }
  }
}
JSON

systemctl restart docker
docker info | grep -i runtimes   # expect runsc to appear
```

Smoke test:

```bash
docker run --rm --runtime=runsc alpine:3 dmesg 2>&1 | head -5
```

If the kernel string mentions gVisor, you're good.

## 2. Install the AppArmor profile (one-time, requires sudo)

The deploy user can't load AppArmor profiles into the kernel — `apparmor_parser`
is a privileged operation. This is a **one-time** setup step. Until it runs,
the controller boots with `apparmor=unconfined` (the GHA deploy default). The
other six hardening levers — cap_drop ALL, no-new-privileges, seccomp,
read_only, internal-network, nonroot uid — still hold, so this is a defence-
in-depth weakening rather than a hole.

```bash
# As root, from the repo root after `git pull`.
install -m 0644 sandbox-controller/deploy/apparmor/codeflow-sandbox-controller \
                /etc/apparmor.d/codeflow-sandbox-controller
apparmor_parser -r /etc/apparmor.d/codeflow-sandbox-controller
aa-status | grep codeflow-sandbox-controller   # expect (enforce)
```

After the profile is loaded, set the GitHub repository variable
`CFSC_APPARMOR_PROFILE=codeflow-sandbox-controller` (or edit `.env.release`
on the host directly). The next deploy picks it up — compose substitutes
`apparmor=${CFSC_APPARMOR_PROFILE}` and the restarted controller enforces
the profile.

## 3. Seccomp profile (automatic, no sudo)

Unlike AppArmor, seccomp profiles are docker-loaded files — dockerd reads
them at container-create time. The deploy workflow scps
`sandbox-controller/deploy/seccomp/controller-seccomp.json` from the repo to
`/opt/codeflow/cfsc/seccomp/cfsc.json` on every deploy. The compose file
references that path via `seccomp=${CFSC_SECCOMP_PROFILE_PATH:-/opt/codeflow/cfsc/seccomp/cfsc.json}`.
**No manual step required** — but if you're standing up the host by hand
before the first deploy:

```bash
sudo install -d -m 0755 /opt/codeflow/cfsc/seccomp
sudo install -m 0644 sandbox-controller/deploy/seccomp/controller-seccomp.json \
                     /opt/codeflow/cfsc/seccomp/cfsc.json
sudo chown -R "$(id -u):$(id -g)" /opt/codeflow/cfsc
```

## 4. Bootstrap the mTLS CA + per-component certs

The controller's mTLS uses an **internal-only** CA — it is never trusted by the public internet, only by the api/worker/controller themselves.

```bash
# On the host, as a deploy user with write access to /opt/codeflow.
sudo mkdir -p /opt/codeflow/cfsc/{config,tls}
sudo mkdir -p /opt/codeflow/cfsc-client/{api,worker}
sudo chown -R "$(id -u):$(id -g)" /opt/codeflow/cfsc /opt/codeflow/cfsc-client

# Generate the CA + server cert + api/worker client certs.
./deploy/sandbox-controller/scripts/bootstrap-ca.sh /opt/codeflow/cfsc

# Distribute the resulting material:
#   /opt/codeflow/cfsc/tls/server.{pem,key}    -> controller server cert
#   /opt/codeflow/cfsc/tls/ca.pem              -> CA root (controller's client_ca_path)
#   /opt/codeflow/cfsc-client/api/{client.pem, client.key, server-ca.pem}
#   /opt/codeflow/cfsc-client/worker/{client.pem, client.key, server-ca.pem}

# Tighten perms so only the docker-managed uid can read the keys.
sudo chmod 0640 /opt/codeflow/cfsc/tls/server.key
sudo chmod 0640 /opt/codeflow/cfsc-client/api/client.key
sudo chmod 0640 /opt/codeflow/cfsc-client/worker/client.key
```

> **Ownership is fixed up automatically.** `bootstrap-ca.sh` writes the files
> as the invoking user (typically root via sudo). The api/worker run as uid
> 1654 and the controller runs as uid 65532 (distroless `nonroot`), so they
> can't read root-owned 0640 files. The `codeflow-init` container in
> `deploy/docker-compose.prod.yml` chowns these dirs to the right uids on
> every `docker compose up`, so you don't need to chown manually. If you
> need to start the controller without compose (rare), apply the chowns by
> hand:
>
> ```bash
> sudo chown -R 65532:65532 /opt/codeflow/cfsc/tls
> sudo chown -R 1654:1654   /opt/codeflow/cfsc-client/api \
>                           /opt/codeflow/cfsc-client/worker
> ```

See [`cert-rotation.md`](cert-rotation.md) for the rotation procedure (90-day default lifetime).

## 5. Place the controller config

```bash
sudo cp sandbox-controller/deploy/controller-config.example.toml \
        /opt/codeflow/cfsc/config/config.toml
sudo "${EDITOR:-vi}" /opt/codeflow/cfsc/config/config.toml
```

Edit:

- `[server] listen` → `0.0.0.0:8443`.
- `[tls]` block → point at `/etc/cfsc-tls/server.{pem,key}` and `/etc/cfsc-tls/ca.pem` (the prod compose mounts the host TLS dir at `/etc/cfsc-tls`, un-nested from `/etc/cfsc`).
- `[tls.allowed_client_subjects]` → confirm `codeflow-api` and `codeflow-worker` entries match what `bootstrap-ca.sh` issued.
- `[runner] docker_socket_path` → `/var/run/docker.sock`.
- `[workspace] workdir_root` → `/opt/codeflow/workdirs` (must match `CODEFLOW_WORKDIRS_DIR` in `.env.release`).
- `[images.allowed]` → at least the images your agent workflows are expected to use. Default is empty; empty allowlists every `/run` to fail with `image_not_allowed`. See [`image-whitelist-updates.md`](image-whitelist-updates.md) for the SIGHUP reload story.
- `[telemetry] otlp_endpoint` → match the existing CodeFlow `Observability__OtlpEndpoint`.

## 6. Capture the host's docker GID into `.env.release`

The deploy workflow appends this automatically (see `.github/workflows/deploy.yml`), but if you're standing the host up by hand:

```bash
echo "DOCKER_GID=$(getent group docker | cut -d: -f3)" >> /opt/codeflow/.env.release
echo "CODEFLOW_SANDBOX_CONTROLLER_IMAGE=ghcr.io/michaeltrefry/codeflow-sandbox-controller:sha-xxxxxxx" >> /opt/codeflow/.env.release
```

## 7. First `compose up`

```bash
cd /opt/codeflow
docker compose --env-file .env.release -f docker-compose.prod.yml up -d codeflow-sandbox-controller
docker logs codeflow-sandbox-controller --tail 50
```

Expected log lines:

```
{"level":"INFO","msg":"sandbox-controller starting","listen":"0.0.0.0:8443","commit":"sha-...","allowed_subjects":2,"allowed_images":N,"workdir_root":"/opt/codeflow/workdirs"}
```

## 8. Verify the hardening posture

Run the verification checklist from [`HARDENING.md`](HARDENING.md):

```bash
# CapEff zero, NoNewPrivs:1, nonroot uid
docker exec codeflow-sandbox-controller cat /proc/self/status | grep -E '^(Cap(Eff|Bnd)|Uid|Gid|NoNewPrivs):'

# Read-only rootfs
docker exec codeflow-sandbox-controller touch /test 2>&1 || echo "OK: read-only"

# AppArmor enforce
sudo aa-status | grep codeflow-sandbox-controller

# Seccomp filter mode 2
sudo cat /proc/$(docker inspect -f '{{.State.Pid}}' codeflow-sandbox-controller)/status | grep Seccomp

# Internal-only network
docker network inspect controller-internal | jq '.[0].Internal'

# Not published to the host
docker port codeflow-sandbox-controller   # expect empty
```

## 9. Bring up api / worker / ui

After the controller is healthy:

```bash
docker compose --env-file .env.release -f docker-compose.prod.yml up -d
docker compose ps
```

The api / worker logs should mention `ContainerTools__Backend=SandboxController` at startup. A `run_container` from an agent workflow now flows through the controller end-to-end.

## Where to go next

- [`cert-rotation.md`](cert-rotation.md) — 90-day cert rotation procedure.
- [`image-whitelist-updates.md`](image-whitelist-updates.md) — adding/removing images at runtime via SIGHUP.
- [`incident-response.md`](incident-response.md) — what to do if the controller is suspected compromised.
- [`HARDENING.md`](../sandbox-controller/deploy/HARDENING.md) — lever-to-threat mapping (sc-538).
