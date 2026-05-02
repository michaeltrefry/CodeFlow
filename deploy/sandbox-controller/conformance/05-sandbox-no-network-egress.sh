#!/usr/bin/env bash
# 05-sandbox-no-network-egress.sh — assert sandbox containers cannot reach
# the public internet. Runs alpine + wget and asserts the wget fails because
# the sandbox is configured with --network=none. Maps to §6.4.
#
# Note: this probe needs a working trace dir on the workdir share. We create
# one on the fly under the configured CFSC_WORKDIRS_DIR (default
# /opt/codeflow/workdirs) so the workspace validator accepts the request.
. "$(dirname "$0")/_lib.sh"
require_command curl python3 jq

CFSC_WORKDIRS_DIR="${CFSC_WORKDIRS_DIR:-/opt/codeflow/workdirs}"
trace="net-probe-$(uuid7)"
mkdir -p "${CFSC_WORKDIRS_DIR}/${trace}/workspace"
trap 'rm -rf "${CFSC_WORKDIRS_DIR}/${trace}"' EXIT

job=$(uuid7)
body=$(cat <<JSON
{
  "jobId": "$job",
  "traceId": "$trace",
  "repoSlug": "workspace",
  "image": "alpine:3",
  "cmd": ["sh", "-c", "wget -q -T 3 -O /dev/null https://1.1.1.1/ && echo REACHED || echo BLOCKED"],
  "limits": { "cpus": 1, "memoryBytes": 268435456, "pids": 64, "timeoutSeconds": 30, "stdoutMaxBytes": 4096, "stderrMaxBytes": 4096 }
}
JSON
)

status=$(cfsc_curl POST /run -H 'Content-Type: application/json' --data "$body" | awk '{print $2}')
[ "$status" = "200" ] || fail "expected 200; got $status (body: $(cat /tmp/cfsc-conformance.body))"

stdout=$(jq -r '.stdout // ""' < /tmp/cfsc-conformance.body)
case "$stdout" in
  *BLOCKED*) ;;
  *REACHED*) fail "sandbox reached the public internet (--network=none not enforced); stdout: $stdout" ;;
  *)         fail "unexpected stdout (the alpine probe should print BLOCKED or REACHED): $stdout" ;;
esac

pass "no_network_egress — sandbox cannot reach public internet (--network=none enforced)"
