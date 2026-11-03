# .claude inventory

| Path | Kind | Purpose |
|------|------|---------|
| `settings.json` | registration | Registers the hooks; sets `BYDLO_SAFE_DEV=1` for agent shells. |
| `hooks/pre-tool-use-network-guard.sh` | PreToolUse (Bash) | Blocks commands that could start tunnels / Zapret or change routes, DNS, services, system proxy, or control existing VPN / mesh clients. |
| `hooks/post-tool-use-check.sh` | PostToolUse (Write/Edit/Bash) | Policy checks: no `AGENTS.md` / `.codex` / `.cursor`, no AI attribution in commits, no hardcoded drive paths. |
| `hooks/stop-build-check.sh` | Stop | Build + isolated unit tests via `scripts/check.sh` (safety lock on). Skips when nothing changed. |
| `skills/safe-check/SKILL.md` | skill | How to build and test safely. |
| `skills/add-engine/SKILL.md` | skill | Checklist for adding a protocol / engine adapter. |
| `skills/dependency-audit/SKILL.md` | skill | Read-only audit of dependencies and upstream engines; manual invocation only (`/dependency-audit`). |

Hooks are Bash scripts. On Windows they need Git Bash (or another bash) on `PATH`.
If a client does not execute registered hooks, run `bash scripts/check.sh` manually.
No other agent-specific directories are allowed (see `CLAUDE.md`).
