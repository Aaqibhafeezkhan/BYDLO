#!/usr/bin/env bash
# PreToolUse (Bash): block commands that could start network engines or change system networking.
# Reads the hook JSON from stdin. Exit code 2 blocks the tool call and shows stderr to the agent.
input="$(cat)"
lower="$(printf '%s' "$input" | tr '\n' ' ' | tr '[:upper:]' '[:lower:]')"

block() {
  echo "BYDLO network guard: blocked a potentially connection-breaking command ($1). See CLAUDE.md section 0." >&2
  exit 2
}

# Only inspect Bash tool calls.
if ! printf '%s' "$lower" | grep -qE '"tool_name"[[:space:]]*:[[:space:]]*"bash"'; then
  exit 0
fi

# Rough extraction of the command field (good enough for pattern matching).
c="$(printf '%s' "$lower" | sed -n 's/.*"command"[[:space:]]*:[[:space:]]*"\(.*\)".*/\1/p')"
[ -z "$c" ] && c="$lower"

has() { printf '%s' "$c" | grep -qE "$1"; }

# Existing VPN / mesh-network clients on the developer machine must not be touched.
has '(tailscale|zerotier-cli|netbird|headscale)[[:space:]]+(up|down|set|logout|switch|leave)|(stop-service|restart-service|net stop|sc stop|sc config|systemctl[[:space:]]+(stop|restart|disable))[^|;&]*(tailscale|zerotier|netbird|wireguard|openvpn)' && block "existing VPN / mesh client control"

# Executing winws or a Flowseal batch file is blocked; mentioning them as arguments (cp, grep, cat) is fine.
exec_prefix='(^|[;&|(]|/[ck][[:space:]]+|start[[:space:]]+|call[[:space:]]+|start-process[[:space:]]+)[[:space:]]*["'"'"']?([^[:space:]"'"'"';&|]*[/\\])?'
has "${exec_prefix}winws(\.exe)?([[:space:]\"']|$)" && block "Zapret / winws"
has "${exec_prefix}(general|service)[^;&|]*\.bat" && block "Zapret strategy / service.bat"

has '(sing-box|xray)(\.exe)?[[:space:]]+run|xray(\.exe)?[[:space:]]+-c|(wg|awg)-quick[[:space:]]+up|/installtunnelservice|openvpn(\.exe)?[[:space:]]+--config|tg-ws-proxy[[:space:]]+--|proxy\.tg_ws_proxy' && block "real tunnel / proxy"

has '(^|[;&|[:space:]])netsh[[:space:]]|route[[:space:]]+(add|delete|change)|ip[[:space:]]+(route|link|addr)[[:space:]]+(add|del|set|flush|change|replace)|iptables|nft[[:space:]]|pfctl|networksetup[[:space:]]+-set|set-dnsclientserveraddress|new-netroute|remove-netroute|set-netadapter|disable-netadapter|ipconfig[[:space:]]+/(release|renew|flushdns)' && block "system network change"

has 'sc(\.exe)?[[:space:]]+(create|delete)|new-service|remove-service' && block "service install/remove"

has '(drivers/etc/hosts|/etc/hosts)[^|]*>|proxyenable|proxyserver' && block "hosts / system proxy"

exit 0
