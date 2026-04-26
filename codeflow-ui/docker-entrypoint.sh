#!/bin/sh
# Template /usr/share/nginx/html/runtime-config.json from env vars at container start so the
# same UI image can serve any environment. The defaults below match the local-dev (no auth)
# posture; production overrides them via OAUTH_AUTHORITY etc.
set -eu

OAUTH_AUTHORITY="${OAUTH_AUTHORITY:-}"
OAUTH_CLIENT_ID="${OAUTH_CLIENT_ID:-codeflow-ui}"
OAUTH_SCOPE="${OAUTH_SCOPE:-openid profile email}"

target="/usr/share/nginx/html/runtime-config.json"

# Use python-style here-doc + sed-free approach: write JSON literally, escape double quotes
# in inputs so an env var containing " can't break out of the JSON string.
escape_json() {
    printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'
}

cat > "${target}" <<EOF
{
  "oauth": {
    "authority": "$(escape_json "${OAUTH_AUTHORITY}")",
    "clientId": "$(escape_json "${OAUTH_CLIENT_ID}")",
    "scope": "$(escape_json "${OAUTH_SCOPE}")"
  }
}
EOF

echo "[codeflow-ui] runtime-config.json regenerated (authority=${OAUTH_AUTHORITY:-<empty>}, clientId=${OAUTH_CLIENT_ID})"
