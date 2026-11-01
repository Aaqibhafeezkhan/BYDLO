#!/usr/bin/env bash
# Self-test for the PreToolUse network guard. Only feeds JSON to the hook; executes nothing it describes.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
hook="$root/.claude/hooks/pre-tool-use-network-guard.sh"
fail=0

check() {
  local expected="$1" cmd="$2"
  local json
  json="$(printf '{"tool_name":"Bash","tool_input":{"command":"%s"}}' "$cmd")"
  printf '%s' "$json" | bash "$hook" >/dev/null 2>&1
  local code=$?
  if [ "$code" != "$expected" ]; then
    echo "FAIL (expected $expected, got $code): $cmd"
    fail=1
  fi
}

# Must be blocked (exit 2)
check 2 './general.bat'
check 2 'cmd /c general (ALT).bat'
check 2 'service.bat admin'
check 2 'bin/winws.exe --wf-tcp=80'
check 2 'sing-box run -c config.json'
check 2 'wg-quick up wg0'
check 2 'netsh interface show'
check 2 'route add 0.0.0.0 mask 0.0.0.0 10.0.0.1'
check 2 'sc create zapret binPath= x'
check 2 'tailscale down'
check 2 'systemctl stop openvpn'

# Must be allowed (exit 0)
check 0 'cp general.bat tests/fixtures/'
check 0 'grep -n winws.exe service.bat'
check 0 'dotnet test BYDLO.slnx'
check 0 'git status'

if [ "$fail" = 0 ]; then
  echo "network guard self-test: OK"
fi
exit "$fail"
