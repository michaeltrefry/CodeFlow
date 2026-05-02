#!/usr/bin/env bash
# 04-forbidden-field-rejected.sh — assert the controller's strict JSON decode
# refuses unknown fields. Defends against schema-bypass / type-confusion
# attacks where the api/worker's serializer ignores a field the controller
# might otherwise honor. Maps to §10.2 ("no unknown fields" rule).
#
# The dangerous fields don't even appear on RunRequest, so a payload that
# tries to set "privileged":true must 400 — and the controller's daemon call
# must never happen.
. "$(dirname "$0")/_lib.sh"
require_command curl python3

job=$(uuid7)
body=$(cat <<JSON
{
  "jobId": "$job",
  "traceId": "trace-conformance",
  "repoSlug": "workspace",
  "image": "alpine:3",
  "cmd": ["echo", "x"],
  "limits": { "cpus": 1, "memoryBytes": 1073741824, "pids": 64, "timeoutSeconds": 10, "stdoutMaxBytes": 1024, "stderrMaxBytes": 1024 },
  "privileged": true
}
JSON
)

status=$(cfsc_curl POST /run -H 'Content-Type: application/json' --data "$body" | awk '{print $2}')
[ "$status" = "400" ] || fail "expected 400 for unknown 'privileged' field; got $status (body: $(cat /tmp/cfsc-conformance.body))"

# The error body should mention the unknown field somehow (Go's json package
# returns a message like 'json: unknown field "privileged"').
grep -q -E 'privileged|unknown' /tmp/cfsc-conformance.body \
  || fail "expected the 400 body to mention privileged/unknown; got: $(cat /tmp/cfsc-conformance.body)"

pass "forbidden_field — privileged:true rejected by the strict JSON decoder before any docker call"
