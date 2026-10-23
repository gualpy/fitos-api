#!/usr/bin/env bash
# Generates random development secrets into the .NET user-secrets store (outside the repository).
# Safe to re-run: existing values are kept unless you pass --rotate.
set -euo pipefail
project="$(dirname "$0")/../src/FitOS.Api"

set_secret() {
  local key="$1" value="$2"
  if [[ "${ROTATE:-0}" == "1" ]] || ! dotnet user-secrets list --project "$project" 2>/dev/null | grep -q "^$key ="; then
    dotnet user-secrets set "$key" "$value" --project "$project" >/dev/null
    echo "set $key"
  else
    echo "kept $key"
  fi
}

[[ "${1:-}" == "--rotate" ]] && ROTATE=1
set_secret "Jwt:SigningKey" "$(openssl rand -base64 48)"
set_secret "Secrets:EncryptionKey" "$(openssl rand -base64 32)"
echo "E-mail: in development, without a mail server, links are written to the API log. To send real mail use the \"Correo\" tab of the platform panel (saved encrypted in the database), or the Email:* user-secrets as a fallback."
echo "Done. The database password goes in ConnectionStrings:Default (user-secrets or env), never in the repo."
