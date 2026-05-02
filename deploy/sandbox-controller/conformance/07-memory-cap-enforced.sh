#!/usr/bin/env bash
# 07-memory-cap-enforced.sh — assert the per-job memory limit is enforced.
# Runs a memory bomb that tries to allocate more than the configured cap;
# expects the OOM-killer to fire (or the kernel to refuse the allocation).
# Maps to §6.4 / 'limits.memoryBytes'.
. "$(dirname "$0")/_lib.sh"
require_command curl python3 jq

CFSC_WORKDIRS_DIR="${CFSC_WORKDIRS_DIR:-/opt/codeflow/workdirs}"
trace="mem-probe-$(uuid7)"
mkdir -p "${CFSC_WORKDIRS_DIR}/${trace}/workspace"
trap 'rm -rf "${CFSC_WORKDIRS_DIR}/${trace}"' EXIT

# 64 MiB cap; bomb tries to write 256 MiB. dd to /dev/null of an in-memory
# allocation via tail -c won't OOM, so use a python snippet that allocates
# bytes and holds them.
job=$(uuid7)
body=$(cat <<JSON
{
  "jobId": "$job",
  "traceId": "$trace",
  "repoSlug": "workspace",
  "image": "python:3.12-alpine",
  "cmd": ["python", "-c", "buf=bytearray(0); chunk=b'\\\\x00'*1024*1024\\nwhile True:\\n  buf+=chunk\\n  if len(buf) > 256*1024*1024: break"],
  "limits": { "cpus": 1, "memoryBytes": 67108864, "pids": 64, "timeoutSeconds": 30, "stdoutMaxBytes": 4096, "stderrMaxBytes": 4096 }
}
JSON
)

status=$(cfsc_curl POST /run -H 'Content-Type: application/json' --data "$body" | awk '{print $2}')
[ "$status" = "200" ] || fail "expected 200; got $status (body: $(cat /tmp/cfsc-conformance.body))"

# Either the container exits non-zero (OOM kill) or the python raises a
# MemoryError. Both prove the cap is in effect.
exit_code=$(jq -r '.exitCode' < /tmp/cfsc-conformance.body)
[ "$exit_code" != "0" ] || fail "memory bomb exited 0 (cap not enforced)"

pass "memory_cap_enforced — 256 MiB allocation under a 64 MiB cap exited non-zero (code $exit_code)"
