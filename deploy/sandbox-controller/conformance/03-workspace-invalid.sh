#!/usr/bin/env bash
# 03-workspace-invalid.sh — assert /run rejects workspace paths that escape the
# configured workdir root. Maps to §9.3 + §10.2 (validator.workspacePath).
#
# Three sub-cases — each must 400 with workspace_invalid:
#   a) traceId contains ".."
#   b) repoSlug contains a path separator
#   c) traceId names a directory that doesn't exist
. "$(dirname "$0")/_lib.sh"
require_command curl python3 jq

probe() {
  local label="$1" trace_id="$2" repo_slug="$3"
  local job; job=$(uuid7)
  local body
  body=$(cat <<JSON
{
  "jobId": "$job",
  "traceId": $(json_esc "$trace_id"),
  "repoSlug": $(json_esc "$repo_slug"),
  "image": "ghcr.io/trefry/dotnet-tester:latest",
  "cmd": ["echo","x"],
  "limits": { "cpus": 1, "memoryBytes": 1073741824, "pids": 64, "timeoutSeconds": 10, "stdoutMaxBytes": 1024, "stderrMaxBytes": 1024 }
}
JSON
)
  local status code
  status=$(cfsc_curl POST /run -H 'Content-Type: application/json' --data "$body" | awk '{print $2}')
  [ "$status" = "400" ] || fail "[$label] expected 400; got $status (body: $(cat /tmp/cfsc-conformance.body))"
  code=$(jq -r '.error.code // empty' < /tmp/cfsc-conformance.body)
  [ "$code" = "workspace_invalid" ] || fail "[$label] expected workspace_invalid; got '$code'"
}

probe "traceId-with-dotdot" "../etc"        "workspace"
probe "repoSlug-with-slash" "trace-a"       "../../passwd"
probe "trace-not-existing"  "definitely-not-a-real-trace-id-2026" "workspace"

pass "workspace_invalid — traversal, separators, and nonexistent traces all rejected with workspace_invalid"
