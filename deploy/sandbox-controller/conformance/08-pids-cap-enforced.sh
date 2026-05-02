#!/usr/bin/env bash
# 08-pids-cap-enforced.sh — assert the per-job pids limit is enforced.
# Runs a fork bomb capped at 32 pids; the bomb's exit code or stderr should
# show fork failures. Maps to §6.4 / 'limits.pids'.
. "$(dirname "$0")/_lib.sh"
require_command curl python3 jq

CFSC_WORKDIRS_DIR="${CFSC_WORKDIRS_DIR:-/opt/codeflow/workdirs}"
trace="pids-probe-$(uuid7)"
mkdir -p "${CFSC_WORKDIRS_DIR}/${trace}/workspace"
trap 'rm -rf "${CFSC_WORKDIRS_DIR}/${trace}"' EXIT

job=$(uuid7)
# Spawn 64 short-lived sleepers in the background; pids cap is 32. Some forks
# must fail. We bound runtime aggressively so the test stays predictable.
body=$(cat <<JSON
{
  "jobId": "$job",
  "traceId": "$trace",
  "repoSlug": "workspace",
  "image": "alpine:3",
  "cmd": ["sh", "-c", "for i in \$(seq 1 64); do (sleep 5) & done; wait; echo DONE"],
  "limits": { "cpus": 1, "memoryBytes": 268435456, "pids": 32, "timeoutSeconds": 30, "stdoutMaxBytes": 4096, "stderrMaxBytes": 4096 }
}
JSON
)

status=$(cfsc_curl POST /run -H 'Content-Type: application/json' --data "$body" | awk '{print $2}')
[ "$status" = "200" ] || fail "expected 200; got $status (body: $(cat /tmp/cfsc-conformance.body))"

stderr=$(jq -r '.stderr // ""' < /tmp/cfsc-conformance.body)
exit_code=$(jq -r '.exitCode' < /tmp/cfsc-conformance.body)

# Either fork failure messages on stderr OR a non-zero exit code is acceptable
# evidence. The exact wording varies (alpine's busybox sh: "Resource temporarily
# unavailable" or "fork: Resource exhausted").
if echo "$stderr" | grep -qiE 'resource|fork|cannot fork'; then
  pass "pids_cap_enforced — pids limit triggered fork failures (stderr: $(echo "$stderr" | head -1 | tr -d '\n'))"
fi
[ "$exit_code" != "0" ] && pass "pids_cap_enforced — fork bomb exited non-zero (code $exit_code) under pids cap"

fail "pids cap may not be enforced; exit_code=$exit_code stderr=$(echo "$stderr" | head -1)"
