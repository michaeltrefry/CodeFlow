#!/usr/bin/env bash
# run-all.sh — orchestrate every conformance probe in this directory.
# Each probe exits 0 on pass, non-zero on fail. We capture both and produce
# a final pass/fail summary suitable for cron-and-archive.
set -u

cd "$(dirname "$0")"

probes=(
  01-mtls-required.sh
  02-image-not-allowed.sh
  03-workspace-invalid.sh
  04-forbidden-field-rejected.sh
  05-sandbox-no-network-egress.sh
  06-gvisor-active.sh
  07-memory-cap-enforced.sh
  08-pids-cap-enforced.sh
  09-cleanup-correctness.sh
  10-git-credential-file-isolated.sh
)

passed=0
failed=0
fail_log=()

for p in "${probes[@]}"; do
  if [ ! -x "$p" ]; then
    chmod +x "$p" 2>/dev/null || true
  fi
  printf '%-44s ... ' "$p"
  if out=$("./$p" 2>&1); then
    echo "$out"
    passed=$((passed + 1))
  else
    echo "$out"
    failed=$((failed + 1))
    fail_log+=("$p")
  fi
done

printf '\n=== conformance summary ===\n'
printf 'PASSED: %d  FAILED: %d  TOTAL: %d\n' "$passed" "$failed" "${#probes[@]}"
printf 'host:   %s\n' "$(hostname)"
printf 'date:   %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"

if [ "$failed" -ne 0 ]; then
  printf '\nfailed probes:\n'
  for f in "${fail_log[@]}"; do
    printf '  - %s\n' "$f"
  done
  exit 1
fi
exit 0
