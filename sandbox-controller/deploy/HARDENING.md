# Sandbox controller — deploy hardening (sc-538)

This is the lever-to-threat mapping for the sandbox-controller container's runtime posture. Every lever is independent; removing any one is a security regression. The threat model is in [`docs/sandbox-executor.md`](../../docs/sandbox-executor.md) (sc-527).

The Phase 1 architecture puts the controller on the same VM as the rest of CodeFlow. The residual risk on Phase 1 is "controller compromise → host-root via the daemon." We can't eliminate that on a single VM (Phase 2 / sc-539 does that by moving the controller to its own machine), but we can shrink the controller's exploit surface aggressively. This file documents how.

## Levers

| # | Lever | Where it lives | What it defends against |
|---|---|---|---|
| 1 | Distroless static base (`gcr.io/distroless/static-debian12:nonroot`) | [`deploy/Dockerfile`](Dockerfile) | No shell, no package manager, no scripting language inside the image. An exploit primitive that needs to drop a shell or run an interpreter has nothing to invoke. |
| 2 | `read_only: true` | [`deploy/compose.prod.snippet.yml`](compose.prod.snippet.yml) | Root filesystem is read-only. Defends against post-exploit persistence (writing a backdoor binary, modifying a config). Writable scratch is `/tmp` tmpfs only, capped at 16 MiB. |
| 3 | `cap_drop: [ALL]` | compose.prod.snippet.yml | The controller needs zero Linux capabilities — the docker socket is its only privileged operation, and that's mediated by daemon ACL, not container caps. Dropping all caps removes the entire suite of capability-based exploit primitives. |
| 4 | `no-new-privileges:true` | compose.prod.snippet.yml | Even if the controller process is compromised, it cannot gain capabilities it didn't start with (no setuid escalation, no file-cap acquisition). |
| 5 | AppArmor profile [`codeflow-sandbox-controller`](apparmor/codeflow-sandbox-controller) | compose `security_opt` | Mediated allowlist of file paths and network operations. An exploited process can only read the binary, the config, the workdir tree (read-only), and `/tmp`; it can only `connect` to the docker socket and `listen`/`accept` on the mTLS port. Anything else (e.g. reading `/etc/passwd`, writing `/var/log`) is denied. |
| 6 | Seccomp profile [`controller-seccomp.json`](seccomp/controller-seccomp.json) | compose `security_opt` | Default action is `SCMP_ACT_ERRNO`. The allowlist is the conservative set a Go static binary running an HTTPS server and dialing one unix socket actually needs. Dangerous syscalls (`mount`, `umount`, `pivot_root`, `kexec_*`, `init_module`, `bpf`, `ptrace`, `process_vm_*`, `keyctl`, `unshare`, `setns`, etc.) are not on the list. |
| 7 | `user: "65532:65532"` (distroless `nonroot`) | compose.prod.snippet.yml | Process inside the container runs as nonroot from PID 1. Defends against any post-exploit primitive that assumes root inside the container. |
| 8 | `tmpfs` for `/tmp` (capped) | compose.prod.snippet.yml | Writable scratch is in-memory only; size cap (16 MiB) defends against a fill-the-disk DoS via `/tmp`. Discarded on container restart. |
| 9 | `controller-internal` network with `internal: true` | compose.prod.snippet.yml | No NAT route from the controller container to the public internet. The controller has zero outbound HTTPS surface. Image pulls happen at dockerd (which is on the host network), not from inside the controller. Exfiltration from a compromised controller has to go through dockerd or the docker socket — both audit-loggable. |
| 10 | No `ports:` directive | compose.prod.snippet.yml | The controller is not published to the host. From outside the compose network it does not exist. Defends against host-side port-scanning and against accidental exposure if the host firewall is misconfigured. |
| 11 | `group_add` for the host's docker gid | compose.prod.snippet.yml | The container runs as nonroot but joins the docker group via supplementary gid, so it can write to `/var/run/docker.sock` (mode 0660 root:docker). Without this the socket mount is read-only-by-accident, which is a reliability issue, not security — listed for completeness. |

## Verification checklist

Run on the executor VM after `docker compose up -d codeflow-sandbox-controller`. Each item maps to one lever above; expect the listed result. Failures mean a lever has been disabled or weakened.

### Inside the running container

```bash
docker exec codeflow-sandbox-controller cat /proc/self/status | grep -E '^(Cap(Inh|Prm|Eff|Bnd|Amb)|Uid|Gid|NoNewPrivs):'
```

Expected:
```
Uid:  65532  65532  65532  65532
Gid:  65532  65532  65532  65532
CapInh:   0000000000000000
CapPrm:   0000000000000000
CapEff:   0000000000000000
CapBnd:   0000000000000000
CapAmb:   0000000000000000
NoNewPrivs:   1
```

(Levers 3, 4, 7. CapEff `0000000000000000` is the headline — the process has zero effective capabilities.)

```bash
docker exec codeflow-sandbox-controller touch /test 2>&1 || echo OK
```

Expected: a "Read-only file system" error (lever 2).

```bash
docker exec codeflow-sandbox-controller sh -c 'exit 0' 2>&1 || echo OK
```

Expected: `OCI runtime exec failed: ... executable file not found in $PATH: unknown` — there is no shell in the image (lever 1).

### From the host

```bash
sudo aa-status | grep codeflow-sandbox-controller
```

Expected: line ending with `(enforce)` and at least one PID. (Lever 5.)

```bash
sudo cat /proc/$(docker inspect -f '{{.State.Pid}}' codeflow-sandbox-controller)/status | grep Seccomp
```

Expected: `Seccomp:   2` (filter mode active; lever 6).

```bash
docker network inspect controller-internal | jq '.[0].Internal'
```

Expected: `true` (lever 9).

```bash
docker network inspect controller-internal | jq -r '.[0].Containers | to_entries[] | .value.Name'
```

Expected: only `codeflow-api`, `codeflow-worker`, `codeflow-sandbox-controller`. Anything else means a service has been attached that shouldn't be.

```bash
docker port codeflow-sandbox-controller
```

Expected: empty output (lever 10 — no published ports).

### From inside the controller (egress check)

```bash
docker exec codeflow-sandbox-controller \
  /usr/local/bin/sandbox-controller -version-only  # works
# A future test stub would attempt: curl https://example.com (must fail / no DNS / no route)
```

Expected: outbound HTTPS to the public internet is impossible (no route, no resolver). Once `sc-534` adds a curl-equipped sidecar for OTLP smoke testing, replace this stub with a real egress check that asserts `EHOSTUNREACH` or DNS failure.

## Tampering checklist (for code review)

When reviewing changes to `compose.prod.snippet.yml`, [`Dockerfile`](Dockerfile), the AppArmor profile, or the seccomp profile, **block** any change that:

- Removes `read_only: true`
- Removes or shrinks `cap_drop: [ALL]`
- Removes `no-new-privileges:true`
- Removes the `apparmor=` or `seccomp=` security_opt entries
- Switches to `user: root` or removes the `user:` entry entirely
- Adds a `ports:` directive
- Removes `internal: true` from the `controller-internal` network
- Adds an outbound network the controller could egress to (e.g. attaching to `default` or `trefry-network`)
- Changes the seccomp `defaultAction` from `SCMP_ACT_ERRNO`
- Adds any of `mount`, `umount*`, `pivot_root`, `chroot`, `bpf`, `ptrace`, `process_vm_*`, `kexec_*`, `init_module`, `delete_module`, `keyctl`, `unshare`, `setns` to the seccomp allowlist
- Loosens any `deny` rule in the AppArmor profile

If you have a real reason to weaken any of the above, leave a comment in the file explaining the rationale and link to the slice / decision that authorized it. There should be no quiet rollbacks.

## Phase 2 carry-over

When sc-539 graduates the controller to its own VM, every lever in this document carries over verbatim. The controller container's runtime posture is the same; what changes is the deploy topology (which VM hosts the container, which network the api/worker reach over). HARDENING.md does not need to be updated for Phase 2 unless we choose to add new levers (e.g. user namespace remapping on the executor VM, which only makes sense once the controller isn't sharing a kernel with CodeFlow).
