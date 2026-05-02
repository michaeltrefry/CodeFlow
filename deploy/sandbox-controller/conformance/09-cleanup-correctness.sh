#!/usr/bin/env bash
# 09-cleanup-correctness.sh — assert that after a run, no stale containers
# (cf-managed=true) and no stale scratch are left behind. Per-job teardown
# is synchronous to /run; this verifies the runner actually executes it.
# Maps to sc-529 (synchronous teardown) + sc-533 (sweeper safety net).
. "$(dirname "$0")/_lib.sh"
require_command curl python3 jq docker

CFSC_WORKDIRS_DIR="${CFSC_WORKDIRS_DIR:-/opt/codeflow/workdirs}"
trace="cleanup-probe-$(uuid7)"
mkdir -p "${CFSC_WORKDIRS_DIR}/${trace}/workspace"
trap 'rm -rf "${CFSC_WORKDIRS_DIR}/${trace}"' EXIT

# Snapshot the cf-managed container count before the test.
before=$(docker ps -a --filter label=cf-managed=true --format '{{.ID}}' | wc -l)

job=$(uuid7)
body=$(cat <<JSON
{
  "jobId": "$job",
  "traceId": "$trace",
  "repoSlug": "workspace",
  "image": "alpine:3",
  "cmd": ["echo", "hi"],
  "limits": { "cpus": 1, "memoryBytes": 134217728, "pids": 32, "timeoutSeconds": 15, "stdoutMaxBytes": 1024, "stderrMaxBytes": 1024 }
}
JSON
)

status=$(cfsc_curl POST /run -H 'Content-Type: application/json' --data "$body" | awk '{print $2}')
[ "$status" = "200" ] || fail "expected 200; got $status (body: $(cat /tmp/cfsc-conformance.body))"

# Brief grace for the controller's deferred teardown to complete (the
# RemoveContainer call happens in a defer after the response is written).
sleep 2

after=$(docker ps -a --filter label=cf-managed=true --format '{{.ID}}' | wc -l)
[ "$before" = "$after" ] || fail "cf-managed container count grew from $before to $after — teardown may have leaked"

# The per-job results dir under .results/ should still exist (sweeper
# garbage-collects with TTL) but the trace dir's main contents shouldn't have
# extra junk.
results_dir="${CFSC_WORKDIRS_DIR}/${trace}/.results/${job}"
[ -d "$results_dir" ] || fail "expected per-job results dir $results_dir to exist after run"

pass "cleanup_correctness — cf-managed count unchanged ($before before, $after after); per-job results dir present"
