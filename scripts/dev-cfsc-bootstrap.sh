#!/bin/sh
# Idempotently generate dev mTLS material + a dev controller config.toml for
# the local docker-compose sandbox-controller stack.
#
# Runs inside an Alpine init container (see docker-compose.yml service
# codeflow-cfsc-init). Output paths:
#   $TLS_DIR/client-ca.pem         CA cert (controllers + clients trust this)
#   $TLS_DIR/client-ca.key         CA private key (don't ship)
#   $TLS_DIR/server.pem            controller server cert (CN=codeflow-sandbox-controller)
#   $TLS_DIR/server.key            controller server key
#   $TLS_DIR/codeflow-api.pem      api client cert (CN=codeflow-api, O=trefry)
#   $TLS_DIR/codeflow-api.key
#   $TLS_DIR/codeflow-worker.pem   worker client cert (CN=codeflow-worker, O=trefry)
#   $TLS_DIR/codeflow-worker.key
#   $CFG_DIR/config.toml           derived dev config pointing at the dev-tls paths
#
# Mirrors sandbox-controller/scripts/gen-dev-certs.sh for the certs and
# sandbox-controller/deploy/controller-config.toml for the config (with paths
# rewritten to /etc/cfsc-tls and the docker.io tester images allowlist trimmed
# to the local-dev base images).

set -eu

TLS_DIR=${TLS_DIR:-/etc/cfsc-tls}
CFG_DIR=${CFG_DIR:-/etc/cfsc}

if ! command -v openssl >/dev/null 2>&1; then
    apk add --no-cache openssl >/dev/null
fi

mkdir -p "$TLS_DIR" "$CFG_DIR"

san_conf() {
    cn=$1
    cat <<CONF
[req]
distinguished_name = dn
req_extensions     = san
prompt             = no
[dn]
CN = $cn
O  = trefry
[san]
subjectAltName = DNS:localhost,DNS:codeflow-sandbox-controller,IP:127.0.0.1
extendedKeyUsage = serverAuth,clientAuth
CONF
}

issue() {
    name=$1
    cn=$2
    if [ -f "$TLS_DIR/$name.pem" ]; then
        echo "$TLS_DIR/$name.pem exists; keeping"
        return
    fi
    conf=$(mktemp)
    san_conf "$cn" > "$conf"
    openssl ecparam -genkey -name prime256v1 -noout -out "$TLS_DIR/$name.key"
    openssl req -new -key "$TLS_DIR/$name.key" -out "$TLS_DIR/$name.csr" -config "$conf"
    openssl x509 -req -in "$TLS_DIR/$name.csr" \
        -CA "$TLS_DIR/client-ca.pem" -CAkey "$TLS_DIR/client-ca.key" -CAcreateserial \
        -out "$TLS_DIR/$name.pem" -days 365 -extensions san -extfile "$conf"
    rm -f "$TLS_DIR/$name.csr" "$conf"
    echo "issued $TLS_DIR/$name.pem"
}

if [ ! -f "$TLS_DIR/client-ca.pem" ]; then
    openssl ecparam -genkey -name prime256v1 -noout -out "$TLS_DIR/client-ca.key"
    openssl req -new -x509 -days 365 -key "$TLS_DIR/client-ca.key" \
        -out "$TLS_DIR/client-ca.pem" \
        -subj "/CN=cfsc-dev-ca/O=trefry-test"
    echo "issued dev CA"
fi

issue server          codeflow-sandbox-controller
issue codeflow-api    codeflow-api
issue codeflow-worker codeflow-worker

# Distroless `nonroot` user (65532) owns the controller's mounted dirs so it
# can read the server cert + key at startup. Best-effort — chown may not work
# on every filesystem; the controller only needs read.
chown -R 65532:65532 "$TLS_DIR" 2>/dev/null || true
chmod 0644 "$TLS_DIR"/*.pem 2>/dev/null || true
# Server key stays group-only; the api + worker containers read DIFFERENT
# client keys (codeflow-api.key / codeflow-worker.key) and run as a separate
# uid (1654 — the .NET `app` user) with no shared group, so those need to be
# world-readable inside the dev volume. The repo .gitignore + the local-only
# named volume keep the file off disk in any real environment; this is dev-
# self-signed material that exists for ~24h.
chmod 0640 "$TLS_DIR"/server.key 2>/dev/null || true
chmod 0640 "$TLS_DIR"/client-ca.key 2>/dev/null || true
chmod 0644 "$TLS_DIR"/codeflow-api.key 2>/dev/null || true
chmod 0644 "$TLS_DIR"/codeflow-worker.key 2>/dev/null || true

if [ ! -f "$CFG_DIR/config.toml" ]; then
    cat > "$CFG_DIR/config.toml" <<'TOML'
# Local-dev sandbox-controller config (managed by scripts/dev-cfsc-bootstrap.sh).
# DO NOT edit by hand; this file is regenerated on every `docker compose up` if
# absent. Production config lives in sandbox-controller/deploy/controller-config.toml.

[server]
listen = "0.0.0.0:8443"

[tls]
cert_path      = "/etc/cfsc-tls/server.pem"
key_path       = "/etc/cfsc-tls/server.key"
client_ca_path = "/etc/cfsc-tls/client-ca.pem"

[[tls.allowed_client_subjects]]
common_name  = "codeflow-api"
organization = "trefry"

[[tls.allowed_client_subjects]]
common_name  = "codeflow-worker"
organization = "trefry"

[logging]
level = "debug"

[runner]
docker_socket_path                = "/var/run/docker.sock"
image_pull_timeout_seconds        = 600
container_teardown_timeout_seconds = 30
# Dev: macOS Docker Desktop has no gVisor; fall back to the default runc
# runtime. Prod overrides to "runsc" via the prod controller-config.toml.
runtime                            = ""

[workspace]
# Dev: identical host & container path so dockerd's bind-mount resolution
# works for the nested sandbox container that container.run launches.
workdir_root = "/tmp/codeflow-workdir"

[[images.allowed]]
registry   = "ghcr.io"
repository = "trefry/*"
tag        = "*"

[[images.allowed]]
registry   = "mcr.microsoft.com"
repository = "dotnet/*"
tag        = "*"

[[images.allowed]]
registry   = "docker.io"
repository = "library/*"
tag        = "*"

[sweeper]
interval_seconds      = 1800
container_ttl_seconds = 86400
results_ttl_seconds   = 604800

[telemetry]
otlp_endpoint   = ""
service_name    = "sandbox-controller"
service_version = ""
TOML
    chown 65532:65532 "$CFG_DIR/config.toml" 2>/dev/null || true
    chmod 0644 "$CFG_DIR/config.toml" 2>/dev/null || true
    echo "wrote $CFG_DIR/config.toml"
fi

echo "dev cfsc bootstrap complete"
ls -la "$TLS_DIR" "$CFG_DIR"
