#!/usr/bin/env bash
# Shared helpers for the conformance probes.
# All probes source this with: . "$(dirname "$0")/_lib.sh"

set -eu

CFSC_URL="${CFSC_URL:-https://codeflow-sandbox-controller:8443}"
CFSC_CLIENT_DIR="${CFSC_CLIENT_DIR:-/opt/codeflow/cfsc-client/api}"
CFSC_CLIENT_PEM="${CFSC_CLIENT_DIR}/client.pem"
CFSC_CLIENT_KEY="${CFSC_CLIENT_DIR}/client.key"
CFSC_SERVER_CA="${CFSC_CLIENT_DIR}/server-ca.pem"

# Print PASS / FAIL line and exit with the right status.
pass() { printf 'PASS: %s\n' "$*"; exit 0; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

# require_files <path>... — fail loudly if any input is missing.
require_files() {
  for f in "$@"; do
    [ -r "$f" ] || fail "required file not readable: $f"
  done
}

# require_command <cmd>... — fail loudly if any binary is absent.
require_command() {
  for c in "$@"; do
    command -v "$c" > /dev/null 2>&1 || fail "required command not on PATH: $c"
  done
}

# cfsc_curl <method> <path> [<extra args>]
# Issues an mTLS request against the controller; echoes the HTTP status code on
# success line "HTTP <code>". Body goes to stdout if --print-body is in extra args.
cfsc_curl() {
  local method="$1"; shift
  local path="$1"; shift
  curl -sS -o /tmp/cfsc-conformance.body -w 'HTTP %{http_code}\n' \
       --cacert "$CFSC_SERVER_CA" \
       --cert   "$CFSC_CLIENT_PEM" \
       --key    "$CFSC_CLIENT_KEY" \
       --resolve "codeflow-sandbox-controller:8443:127.0.0.1" \
       -X "$method" \
       "${CFSC_URL}${path}" \
       "$@"
}

# uuid7 emits a random UUIDv7-shaped string. We don't validate the timestamp
# bits — the controller treats any UUID as opaque.
uuid7() { python3 -c "import uuid; print(uuid.uuid4())"; }

# json escape a string suitable for embedding in a JSON string literal.
json_esc() { python3 -c 'import json,sys;print(json.dumps(sys.argv[1]))' "$1"; }
