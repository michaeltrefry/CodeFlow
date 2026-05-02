# mTLS cert rotation runbook (sc-535)

The sandbox-controller's mTLS material is issued from an internal-only CA. Default leaf-cert lifetime is **90 days**; rotation is currently **manual** for Phase 1 (sc-539's graduation slice introduces automation). Operators set a calendar reminder ~7 days before expiry.

## What lives where

| Material | Path on host | Mounted into | Used as |
|---|---|---|---|
| CA root cert | `/opt/codeflow/cfsc/tls/ca.pem` | `/etc/cfsc/tls/ca.pem` (controller) | controller `ClientCAPath` (verifies api/worker client certs) |
| CA root key | `/opt/codeflow/cfsc/tls/ca.key` | not mounted | issuance only |
| Server cert + key | `/opt/codeflow/cfsc/tls/server.{pem,key}` | `/etc/cfsc/tls/` | controller server cert |
| api client cert + key | `/opt/codeflow/cfsc-client/api/client.{pem,key}` | `/etc/cfsc-client/` (api) | api's mTLS client cert |
| api server CA | `/opt/codeflow/cfsc-client/api/server-ca.pem` | `/etc/cfsc-client/` (api) | api's `ServerCAPath` |
| worker client cert + key | `/opt/codeflow/cfsc-client/worker/client.{pem,key}` | `/etc/cfsc-client/` (worker) | worker's mTLS client cert |
| worker server CA | `/opt/codeflow/cfsc-client/worker/server-ca.pem` | `/etc/cfsc-client/` (worker) | worker's `ServerCAPath` |

## Routine rotation (no compromise)

Re-issue **all leaves**; keep the CA root unchanged. Server and client leaves are independent — failure of one doesn't invalidate the others.

```bash
cd /opt/codeflow
./bootstrap-ca.sh /opt/codeflow/cfsc   # idempotent on the CA, re-issues leaves

# Validate the new server cert presents correctly.
openssl x509 -in /opt/codeflow/cfsc/tls/server.pem -noout -dates -subject -issuer

# Restart the controller. api/worker reconnect on next request — no restart
# needed for them.
docker compose --env-file .env.release -f docker-compose.prod.yml restart codeflow-sandbox-controller
docker logs codeflow-sandbox-controller --tail 20
```

## CA root rotation (3-year lifetime)

Materially harder than leaf rotation because the api/worker have the old CA pinned in their `ServerCAPath`. The naive flip breaks every in-flight request.

The recommended sequence is a **dual-trust bridge**:

1. Issue the new CA: keep `ca.pem`/`ca.key` as the OLD root, generate `ca-new.pem`/`ca-new.key`.
2. Bundle both: `cat ca.pem ca-new.pem > ca-bundle.pem`.
3. Distribute `ca-bundle.pem` as the api/worker `server-ca.pem` (replaces the single-cert file).
4. Re-issue the controller's server cert under the **new** CA, and the api/worker client certs under the **new** CA too.
5. Restart controller + api + worker.
6. Verify mTLS still succeeds end-to-end (`gh api … sandbox-controller/_status` from a smoke test, or trigger a real `run_container`).
7. After a soak period, swap the api/worker `server-ca.pem` from the bundle to just `ca-new.pem`, delete the OLD CA's key, archive the OLD CA's cert.

## Compromise rotation

If a leaf or the CA is suspected compromised:

1. Treat the host as compromised. Don't trust local diagnostics.
2. Issue a NEW CA on a different machine (a clean ops laptop). New CA, new server cert, new api/worker client certs.
3. Provision the new material to a fresh executor host (ideally a fresh VM if Phase 2 is in scope; on Phase 1, this means a full host rebuild).
4. Update CodeFlow secrets to point at the new server CA + new client cert / key.
5. Revoke the OLD CA in any places it was distributed — though we don't operate a CRL today, document the decision in your incident postmortem so it isn't accidentally re-introduced.

For the more general "what to do if the controller itself is compromised" path, see [`incident-response.md`](incident-response.md).

## Phase 2 (sc-539) automation hook

The graduation slice replaces this manual procedure with cert-manager-style auto-rotation. The interface above is preserved:

- Material lives at the same paths.
- The controller still picks them up via the same compose mounts.
- The api/worker still pin `ServerCommonName` regardless of how the cert was issued.

So the rotation tooling is the only thing that changes when Phase 2 lands; nothing in the application code does.
