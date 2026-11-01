---
name: safe-check
description: Build BYDLO and run the isolated unit tests with the network safety lock on. Use after code changes or before committing. Never starts engines or touches system networking.
---

# safe-check

1. `source scripts/env.sh` (PowerShell: `. ./scripts/env.ps1`) — keeps caches in `.tmp/` and sets `BYDLO_SAFE_DEV=1`.
2. `bash scripts/check.sh` (PowerShell: `./scripts/check.ps1`) — restore, Release build of the desktop projects, unit tests.
3. Report the real result (pass/fail counts). Never claim checks that did not run.

Never run the app with real engines; never run Zapret / tg-ws-proxy / VPN binaries; never change DNS, routes,
firewall, system proxy, or existing VPN / mesh-network clients. To look at the UI, start the desktop app in Mock Mode only:
`dotnet run --project src/Bydlo.Desktop -- --mock`.
