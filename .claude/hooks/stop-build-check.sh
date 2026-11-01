#!/usr/bin/env bash
# Stop hook: build + isolated unit tests with the network safety lock enabled.
# Never launches engines. Skips when nothing in src/tests changed since the last successful check.
input="$(cat)"
if printf '%s' "$input" | grep -qE '"stop_hook_active"[[:space:]]*:[[:space:]]*true'; then
  exit 0
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
command -v dotnet >/dev/null 2>&1 || exit 0
[ -f "$root/BYDLO.slnx" ] || exit 0

# shellcheck disable=SC1091
source "$root/scripts/env.sh"
stamp="$root/.tmp/last-check.stamp"
if [ -f "$stamp" ]; then
  changed="$(find "$root/src" "$root/tests" -newer "$stamp" -type f \
    \( -name '*.cs' -o -name '*.axaml' -o -name '*.csproj' -o -name '*.json' \) \
    -not -path '*/obj/*' -not -path '*/bin/*' 2>/dev/null | head -1)"
  [ -z "$changed" ] && exit 0
fi

log="$root/.tmp/last-check.log"
if bash "$root/scripts/check.sh" >"$log" 2>&1; then
  touch "$stamp"
  exit 0
fi
echo "BYDLO Stop check failed (build or tests). Tail of .tmp/last-check.log:" >&2
tail -n 40 "$log" >&2
exit 2
