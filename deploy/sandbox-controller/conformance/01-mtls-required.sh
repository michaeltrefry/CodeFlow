#!/usr/bin/env bash
# 01-mtls-required.sh — assert /healthz cannot be reached without a client cert.
# Maps to docs/sandbox-executor.md §11.5 (TLS 1.3, RequireAndVerifyClientCert).
. "$(dirname "$0")/_lib.sh"
require_command curl

# Try /healthz with NO client cert. The TLS handshake should fail.
if curl -sS -o /dev/null -w '%{http_code}\n' \
     --cacert "$CFSC_SERVER_CA" \
     --resolve "codeflow-sandbox-controller:8443:127.0.0.1" \
     "${CFSC_URL}/healthz" 2>/dev/null; then
  fail "controller accepted /healthz without a client cert (TLS handshake should have failed)"
fi

# Sanity: the WITH-cert path still works (rules out a network-level false negative).
status=$(cfsc_curl GET /healthz | awk '{print $2}')
[ "$status" = "200" ] || fail "control test (with cert) did not return 200; got $status"

pass "mTLS required — plaintext /healthz rejected at handshake; cert-bearing /healthz returns 200"
