#!/usr/bin/env bash
# Generate self-signed mTLS material for local sandbox-controller development.
#
# Output (under sandbox-controller/deploy/dev-tls/):
#   client-ca.pem        - dev CA cert (also used as the server cert's CA)
#   client-ca.key        - dev CA private key
#   server.pem           - controller's server cert (CN=codeflow-sandbox-controller)
#   server.key           - controller's server key
#   codeflow-api.pem     - client cert (CN=codeflow-api, O=trefry)
#   codeflow-api.key
#   codeflow-worker.pem  - client cert (CN=codeflow-worker, O=trefry)
#   codeflow-worker.key
#
# Run:   ./scripts/gen-dev-certs.sh
# Cleanup: rm -rf deploy/dev-tls
#
# DO NOT use these in production. The CA root is in plaintext in this directory
# and the leaf certs are valid for 365 days with no revocation story.

set -euo pipefail

cd "$(dirname "$0")/.."
TLS=deploy/dev-tls
mkdir -p "$TLS"

# Where openssl req takes its DN. Avoid having to set CN on the command line.
gen_san_conf() {
  local cn="$1"
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

# 1) CA
if [[ ! -f "$TLS/client-ca.pem" ]]; then
  openssl ecparam -genkey -name prime256v1 -noout -out "$TLS/client-ca.key"
  openssl req -new -x509 -days 365 -key "$TLS/client-ca.key" \
    -out "$TLS/client-ca.pem" \
    -subj "/CN=cfsc-dev-ca/O=trefry-test"
fi

issue() {
  local name="$1" cn="$2"
  if [[ -f "$TLS/$name.pem" ]]; then
    echo "$TLS/$name.pem already exists; skipping"
    return
  fi
  local conf
  conf="$(mktemp)"
  gen_san_conf "$cn" > "$conf"
  openssl ecparam -genkey -name prime256v1 -noout -out "$TLS/$name.key"
  openssl req -new -key "$TLS/$name.key" -out "$TLS/$name.csr" -config "$conf"
  openssl x509 -req -in "$TLS/$name.csr" \
    -CA "$TLS/client-ca.pem" -CAkey "$TLS/client-ca.key" -CAcreateserial \
    -out "$TLS/$name.pem" -days 365 -extensions san -extfile "$conf"
  rm -f "$TLS/$name.csr" "$conf"
}

issue server          "codeflow-sandbox-controller"
issue codeflow-api    "codeflow-api"
issue codeflow-worker "codeflow-worker"

echo
echo "dev mTLS material in $TLS/"
ls "$TLS"
