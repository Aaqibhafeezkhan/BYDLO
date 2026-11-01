#!/usr/bin/env bash
# PostToolUse (Write|Edit|Bash): cheap, safe repository policy checks.
# Never runs engines and never touches the network.
input="$(cat)"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
problems=()

for forbidden in AGENTS.md .agents .codex .Codex .cursor; do
  [ -e "$root/$forbidden" ] && problems+=("forbidden agent-specific path exists: $forbidden (CLAUDE.md is the only instruction file)")
done

if printf '%s' "$input" | grep -q 'git commit'; then
  if git -C "$root" rev-parse --verify HEAD >/dev/null 2>&1; then
    msg="$(git -C "$root" log -1 --format=%B 2>/dev/null)"
    if printf '%s' "$msg" | grep -qiE 'co-authored-by:.*(claude|anthropic|openai|copilot|gpt|codex|noreply@anthropic)|generated with \[?claude'; then
      problems+=("last commit message contains AI attribution - forbidden by CLAUDE.md section 6")
    fi
  fi
fi

# Hardcoded absolute drive paths in C# sources.
hits="$(grep -rnE '"[A-Za-z]:\\\\' "$root/src" "$root/tests" --include='*.cs' 2>/dev/null | head -3)"
[ -n "$hits" ] && problems+=("possible hardcoded absolute path: $hits")

if [ ${#problems[@]} -gt 0 ]; then
  printf 'BYDLO policy check:\n' >&2
  printf ' - %s\n' "${problems[@]}" >&2
  exit 2
fi
exit 0
