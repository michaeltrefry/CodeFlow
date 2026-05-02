#!/usr/bin/env bash
# audit-no-dood-on-app-tier.sh — Defence against accidental Docker-out-of-Docker
# regressions on the CodeFlow api/worker (sc-537).
#
# Background: the sandbox controller (epic 526) exists specifically because
# mounting /var/run/docker.sock into a public-facing service makes any code-
# execution flaw in the .NET process host-root on the CodeFlow VM. The
# controller is a separate hardened service whose entire purpose is being the
# only thing that talks to dockerd. The api/worker MUST NOT regain docker
# socket access — re-introducing it would invalidate the threat-model
# discipline that sc-526 / sc-527 / sc-538 invested in.
#
# This script greps for the dangerous patterns in the right places and exits
# non-zero if any of them are found. Run it in CI alongside the build.

set -eu

cd "$(git rev-parse --show-toplevel)"

violations=()

# 1. Api/Worker Dockerfiles must not COPY in the docker CLI binary.
for dockerfile in CodeFlow.Api/Dockerfile CodeFlow.Worker/Dockerfile; do
  if [[ -f "$dockerfile" ]]; then
    if grep -qE 'COPY .*--from=docker:|RUN .*apt.* docker-ce|/usr/local/bin/docker[^a-z-]' "$dockerfile"; then
      violations+=("$dockerfile: docker CLI install detected (sc-537 forbids re-introducing DooD on the app tier)")
    fi
  fi
done

# 2. Api/Worker compose services must not mount /var/run/docker.sock or carry
#    a group_add for the docker gid. Use yq if available; fall back to grep.
COMPOSE=deploy/docker-compose.prod.yml
if [[ -f "$COMPOSE" ]]; then
  if command -v yq > /dev/null 2>&1; then
    for svc in codeflow-api codeflow-worker codeflow-ui; do
      if yq -e ".services.\"$svc\".volumes // [] | any(test(\"docker.sock\"))" "$COMPOSE" > /dev/null 2>&1; then
        violations+=("$COMPOSE: $svc mounts /var/run/docker.sock — must not")
      fi
      if yq -e ".services.\"$svc\".group_add // [] | length > 0" "$COMPOSE" > /dev/null 2>&1; then
        # group_add itself isn't proof of DooD, but on api/worker today there's no legitimate
        # reason to set it. Surface as a violation; whitelist via comment if a reason emerges.
        violations+=("$COMPOSE: $svc has group_add — review intent (DooD on app tier is forbidden)")
      fi
    done
  else
    # Coarser fallback: any docker.sock anywhere in the file is a flag for review.
    # The sandbox-controller service is allowed to mount it — exclude that block.
    if awk '/^  codeflow-(api|worker|ui):/{in_app=1} /^  codeflow-(sandbox-controller|init):/{in_app=0} /^  [a-z]/{if(!/codeflow-(api|worker|ui|sandbox-controller|init):/) in_app=0} in_app && /docker\.sock/' "$COMPOSE" | grep -q .; then
      violations+=("$COMPOSE: api/worker/ui section mentions docker.sock — review intent")
    fi
  fi
fi

# 3. Audit any other compose / dockerfile files we ship.
while IFS= read -r f; do
  case "$f" in
    deploy/docker-compose.prod.yml) continue ;;        # handled above
    sandbox-controller/*) continue ;;                  # controller LEGITIMATELY mounts the socket
    */sandbox-controller/*) continue ;;
  esac
  if [[ "$f" == *Dockerfile* ]] || [[ "$f" == *compose*.yml ]]; then
    if grep -qE 'COPY .*--from=docker:|/var/run/docker\.sock' "$f" 2>/dev/null; then
      violations+=("$f: docker socket mount or CLI install outside of sandbox-controller subtree")
    fi
  fi
done < <(git ls-files)

if [[ ${#violations[@]} -ne 0 ]]; then
  echo "FAIL: DooD-on-app-tier audit found ${#violations[@]} violation(s):" >&2
  for v in "${violations[@]}"; do
    echo "  - $v" >&2
  done
  echo "" >&2
  echo "See docs/sandbox-executor.md and deploy/sandbox-controller/incident-response.md for context." >&2
  echo "If a violation is intentional, document the rationale next to it AND update this script's whitelist." >&2
  exit 1
fi

echo "PASS: no DooD-on-app-tier vestiges detected"
