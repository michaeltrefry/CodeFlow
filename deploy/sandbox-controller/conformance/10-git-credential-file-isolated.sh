#!/usr/bin/env bash
# 10-git-credential-file-isolated.sh — assert the per-trace git-credential file
# (epic 658, sc-660) lives outside WorkingDirectoryRoot at mode 0600 owned by
# the app uid, so the agent's path-confined workspace tools can never reach it
# and only the worker process can read it.
#
# Inputs (env-overridable, with sane defaults matching the deploy/.env shape):
#   CODEFLOW_WORKDIRS_DIR    — where per-trace workdirs live (default /opt/codeflow/workdirs)
#   CODEFLOW_GIT_CREDS_DIR   — where per-trace cred files live (default /var/lib/codeflow/git-creds)
#   APP_UID                  — uid the api/worker run as (default 1654)
#
# This is a structural check: it does NOT require a running controller / api,
# it only inspects the host filesystem. Run on every host that mounts the cred
# volume.
. "$(dirname "$0")/_lib.sh"
require_command stat

CODEFLOW_WORKDIRS_DIR="${CODEFLOW_WORKDIRS_DIR:-/opt/codeflow/workdirs}"
CODEFLOW_GIT_CREDS_DIR="${CODEFLOW_GIT_CREDS_DIR:-/var/lib/codeflow/git-creds}"
APP_UID="${APP_UID:-1654}"

# --- structural: cred root exists, is a directory, owned by APP_UID, mode 0700 ---
[ -d "$CODEFLOW_GIT_CREDS_DIR" ] \
  || fail "git-cred root $CODEFLOW_GIT_CREDS_DIR does not exist (Dockerfile pre-create skipped or volume not mounted)"

dir_uid=$(stat -c '%u' "$CODEFLOW_GIT_CREDS_DIR" 2>/dev/null || stat -f '%u' "$CODEFLOW_GIT_CREDS_DIR")
[ "$dir_uid" = "$APP_UID" ] \
  || fail "git-cred root $CODEFLOW_GIT_CREDS_DIR owner is uid $dir_uid; expected $APP_UID (worker process won't be able to write)"

dir_mode=$(stat -c '%a' "$CODEFLOW_GIT_CREDS_DIR" 2>/dev/null || stat -f '%Lp' "$CODEFLOW_GIT_CREDS_DIR")
[ "$dir_mode" = "700" ] \
  || fail "git-cred root $CODEFLOW_GIT_CREDS_DIR mode is $dir_mode; expected 700"

# --- structural: cred root must NOT be inside WorkingDirectoryRoot ---
# An agent's run_command is cwd-jailed inside its trace workdir; read_file is
# path-confined to the same root. If the cred root sat inside WorkingDirectoryRoot,
# the agent could traverse to it. Resolve symlinks on both sides before the check.
workdirs_real=$(cd "$CODEFLOW_WORKDIRS_DIR" 2>/dev/null && pwd -P) || workdirs_real="$CODEFLOW_WORKDIRS_DIR"
creds_real=$(cd "$CODEFLOW_GIT_CREDS_DIR" 2>/dev/null && pwd -P) || creds_real="$CODEFLOW_GIT_CREDS_DIR"
case "$creds_real/" in
  "$workdirs_real"/*)
    fail "git-cred root ($creds_real) is inside WorkingDirectoryRoot ($workdirs_real); this defeats the credential-helper boundary";;
esac

# --- per-trace files: every file in the cred root must be mode 0600 owned by APP_UID ---
# A non-conforming file leaks plain-text tokens to other uids. Tolerate an empty
# directory (no traces have run yet) — the structural checks above are still meaningful.
fail_files=()
shopt -s nullglob
for f in "$CODEFLOW_GIT_CREDS_DIR"/*; do
  [ -f "$f" ] || continue
  fmode=$(stat -c '%a' "$f" 2>/dev/null || stat -f '%Lp' "$f")
  fuid=$(stat -c '%u' "$f" 2>/dev/null || stat -f '%u' "$f")
  if [ "$fmode" != "600" ] || [ "$fuid" != "$APP_UID" ]; then
    fail_files+=("$(basename "$f"): mode=$fmode uid=$fuid")
  fi
done
shopt -u nullglob

if [ ${#fail_files[@]} -gt 0 ]; then
  printf 'FAIL: per-trace cred file(s) violate the 0600 / uid=%s contract:\n' "$APP_UID" >&2
  for line in "${fail_files[@]}"; do
    printf '  - %s\n' "$line" >&2
  done
  exit 1
fi

pass "git_credential_file_isolated — root $CODEFLOW_GIT_CREDS_DIR mode $dir_mode uid $dir_uid; outside $workdirs_real; per-trace files all 0600/uid=$APP_UID"
