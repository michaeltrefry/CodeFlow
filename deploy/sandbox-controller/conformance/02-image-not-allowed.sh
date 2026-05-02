#!/usr/bin/env bash
# 02-image-not-allowed.sh — assert /run rejects images that aren't on the
# controller's allowlist. Maps to §10.2 (default-deny image policy) and the
# rejected-request log discipline in §10.4 (no raw image string in the log).
. "$(dirname "$0")/_lib.sh"
require_command curl python3 jq

job=$(uuid7)
body=$(cat <<JSON
{
  "jobId": "$job",
  "traceId": "trace-conformance-$job",
  "repoSlug": "workspace",
  "image": "attacker.example/probe:latest",
  "cmd": ["echo", "should not run"],
  "limits": { "cpus": 1, "memoryBytes": 1073741824, "pids": 64, "timeoutSeconds": 10, "stdoutMaxBytes": 1024, "stderrMaxBytes": 1024 }
}
JSON
)

status=$(cfsc_curl POST /run -H 'Content-Type: application/json' --data "$body" | awk '{print $2}')
[ "$status" = "403" ] || fail "expected 403 for non-allowlisted image; got $status (body: $(cat /tmp/cfsc-conformance.body))"

code=$(jq -r '.error.code // empty' < /tmp/cfsc-conformance.body)
[ "$code" = "image_not_allowed" ] || fail "expected error.code=image_not_allowed; got '$code'"

pass "image_not_allowed — non-allowlisted image rejected with 403 image_not_allowed"
