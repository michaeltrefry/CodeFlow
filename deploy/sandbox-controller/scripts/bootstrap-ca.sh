#!/usr/bin/env bash
# Bootstrap an internal mTLS CA + server cert + per-component client certs for
# the sandbox controller (sc-535).
#
# Output (under $1, default /opt/codeflow/cfsc):
#   tls/ca.pem        + tls/ca.key                        — internal CA root
#   tls/server.pem    + tls/server.key                    — controller server cert
#   ../cfsc-client/api/client.pem + .../client.key        — codeflow-api client cert
#   ../cfsc-client/api/server-ca.pem                      — pin
#   ../cfsc-client/worker/client.pem + .../client.key     — codeflow-worker client cert
#   ../cfsc-client/worker/server-ca.pem                   — pin
#
# Cert lifetime: 90 days. Rotation procedure in cert-rotation.md.
#
# Usage:
#   ./deploy/sandbox-controller/scripts/bootstrap-ca.sh [/opt/codeflow/cfsc]
#
# Idempotent on the CA itself — won't regenerate if tls/ca.pem already exists.
# Always re-issues server + client leaf certs.

set -euo pipefail

CFSC_ROOT="${1:-/opt/codeflow/cfsc}"
CLIENT_ROOT="$(dirname "$CFSC_ROOT")/cfsc-client"

mkdir -p "$CFSC_ROOT/tls"
mkdir -p "$CLIENT_ROOT/api"
mkdir -p "$CLIENT_ROOT/worker"

VALIDITY_DAYS=90

# --- 1. CA root ----------------------------------------------------------
if [[ ! -f "$CFSC_ROOT/tls/ca.pem" ]]; then
  echo "[bootstrap-ca] generating CA root in $CFSC_ROOT/tls/"
  openssl ecparam -genkey -name prime256v1 -noout -out "$CFSC_ROOT/tls/ca.key"
  chmod 0600 "$CFSC_ROOT/tls/ca.key"
  openssl req -new -x509 -days 3650 -key "$CFSC_ROOT/tls/ca.key" \
    -out "$CFSC_ROOT/tls/ca.pem" \
    -subj "/CN=cfsc-internal-ca/O=trefry/OU=codeflow-sandbox"
else
  echo "[bootstrap-ca] reusing existing CA at $CFSC_ROOT/tls/ca.pem"
fi

ca_cert="$CFSC_ROOT/tls/ca.pem"
ca_key="$CFSC_ROOT/tls/ca.key"

issue_cert() {
  local name="$1" cn="$2" org="$3" key_path="$4" cert_path="$5" san="$6" eku="$7"
  local conf
  conf="$(mktemp)"
  cat > "$conf" <<CONF
[req]
distinguished_name = dn
req_extensions     = v3
prompt             = no

[dn]
CN = $cn
O  = $org

[v3]
subjectAltName  = $san
extendedKeyUsage = $eku
basicConstraints = CA:FALSE
CONF
  openssl ecparam -genkey -name prime256v1 -noout -out "$key_path"
  chmod 0640 "$key_path"
  local csr
  csr="$(mktemp)"
  openssl req -new -key "$key_path" -out "$csr" -config "$conf"
  openssl x509 -req -in "$csr" \
    -CA "$ca_cert" -CAkey "$ca_key" -CAcreateserial \
    -out "$cert_path" -days "$VALIDITY_DAYS" \
    -extensions v3 -extfile "$conf"
  rm -f "$csr" "$conf"
  echo "[bootstrap-ca] issued $name -> $cert_path (valid $VALIDITY_DAYS days)"
}

# --- 2. Server cert ------------------------------------------------------
issue_cert "server" \
  "codeflow-sandbox-controller" \
  "trefry" \
  "$CFSC_ROOT/tls/server.key" \
  "$CFSC_ROOT/tls/server.pem" \
  "DNS:codeflow-sandbox-controller,DNS:localhost,IP:127.0.0.1" \
  "serverAuth"

# --- 3. Client certs (api + worker) -------------------------------------
issue_cert "codeflow-api" \
  "codeflow-api" \
  "trefry" \
  "$CLIENT_ROOT/api/client.key" \
  "$CLIENT_ROOT/api/client.pem" \
  "DNS:codeflow-api" \
  "clientAuth"
cp "$ca_cert" "$CLIENT_ROOT/api/server-ca.pem"

issue_cert "codeflow-worker" \
  "codeflow-worker" \
  "trefry" \
  "$CLIENT_ROOT/worker/client.key" \
  "$CLIENT_ROOT/worker/client.pem" \
  "DNS:codeflow-worker" \
  "clientAuth"
cp "$ca_cert" "$CLIENT_ROOT/worker/server-ca.pem"

# --- 4. Summary ----------------------------------------------------------
echo
echo "[bootstrap-ca] done. Place these in compose's volume mounts:"
echo "  controller:  $CFSC_ROOT/tls/{server.pem,server.key,ca.pem}  ->  /etc/cfsc/tls/"
echo "  api:         $CLIENT_ROOT/api/{client.pem,client.key,server-ca.pem}  ->  /etc/cfsc-client/"
echo "  worker:      $CLIENT_ROOT/worker/{client.pem,client.key,server-ca.pem}  ->  /etc/cfsc-client/"
echo
echo "Rotation: re-run this script before $VALIDITY_DAYS days are up; see cert-rotation.md."
