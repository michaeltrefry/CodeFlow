#!/usr/bin/env bash
# 06-gvisor-active.sh — assert sandbox containers run under gVisor (runsc),
# not under the host kernel via runc. The check: read /proc/version from
# inside the sandbox and confirm it identifies as gVisor. Maps to §6.4.
. "$(dirname "$0")/_lib.sh"
require_command curl python3 jq

CFSC_WORKDIRS_DIR="${CFSC_WORKDIRS_DIR:-/opt/codeflow/workdirs}"
trace="gvisor-probe-$(uuid7)"
mkdir -p "${CFSC_WORKDIRS_DIR}/${trace}/workspace"
trap 'rm -rf "${CFSC_WORKDIRS_DIR}/${trace}"' EXIT

job=$(uuid7)
body=$(cat <<JSON
{
  "jobId": "$job",
  "traceId": "$trace",
  "repoSlug": "workspace",
  "image": "alpine:3",
  "cmd": ["cat", "/proc/version"],
  "limits": { "cpus": 1, "memoryBytes": 268435456, "pids": 64, "timeoutSeconds": 15, "stdoutMaxBytes": 4096, "stderrMaxBytes": 4096 }
}
JSON
)

status=$(cfsc_curl POST /run -H 'Content-Type: application/json' --data "$body" | awk '{print $2}')
[ "$status" = "200" ] || fail "expected 200; got $status (body: $(cat /tmp/cfsc-conformance.body))"

stdout=$(jq -r '.stdout // ""' < /tmp/cfsc-conformance.body)
# gVisor's /proc/version contains "gVisor". A runc fallback would expose the
# host's actual Linux kernel string instead.
case "$stdout" in
  *[Gg][Vv]isor*) pass "gvisor_active — /proc/version: $(printf '%s' "$stdout" | head -1)" ;;
  *)              fail "/proc/version did not identify as gVisor (got: $stdout). Check daemon.json runtime registration and that the controller passes --runtime=runsc." ;;
esac
