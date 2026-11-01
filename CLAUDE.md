# CLAUDE.md — BYDLO project rules

BYDLO — **B**ypass **Y**our **D**igital **L**imitations **O**nline.
*One app. Every protocol. Zero bullshit.*

This file is the **single** instruction file for coding agents working on this repository.
Never create `AGENTS.md` or any other parallel instruction file.

---

## 0. Network safety (highest priority)

Contributors' machines may depend on their current network setup (existing VPNs, mesh networks,
remote access). Development work must never break the developer's connectivity.

Agents MUST NOT, under any circumstances:

- Run Zapret, `winws.exe`, WinDivert, Flowseal strategies (`general*.bat`, `service.bat`) or their tests.
- Run real VPN tunnels or proxies (sing-box, Xray, WireGuard, AmneziaWG, OpenVPN, tg-ws-proxy) for testing.
- Change routes, DNS, network interfaces, firewall, `hosts`, system proxy settings or drivers.
- Stop, restart or reconfigure VPN / mesh-network clients already installed on the machine.
- Install or remove system network services.
- Run any command or script that could break the current connection.
- Automatically verify that a censorship-bypass method works.

Allowed: reading docs, reading source code, fetching dependencies, static analysis, compiling,
and isolated tests that do not touch system networking.

Safety mechanisms in the code base (keep them working):

- `BYDLO_SAFE_DEV=1` (set by `scripts/env.sh` / `scripts/env.ps1` and by every hook) makes
  `NetworkSafetyGuard` refuse to launch any real engine or privileged operation.
- **Mock Mode** (`--mock` flag, `BYDLO_MOCK=1`, or Settings → Advanced) replaces every engine with
  `MockEngine`. Use it for every UI / flow test. Mock Mode is a *test tool*, never a feature claim.
- Unit tests use `FakeProcessRunner` and fake HTTP handlers. Tests must never spawn engines.

If a test could possibly affect the connection — **do not run it**. The maintainer runs real network tests manually.

## 1. Repository layout

```
src/Bydlo.Core        Shared business logic: models, import, config builders, engines API,
                      connection manager, mock engine, security, process supervision.
src/Bydlo.Engines     Real engine adapters (sing-box, Xray, WireGuard, AmneziaWG, OpenVPN,
                      Zapret (Flowseal), TgWsProxy (Flowseal)) + engine installer.
src/Bydlo.Platform    Platform services: paths, secret storage, privileges, system proxy.
src/Bydlo.App         Cross-platform Avalonia UI (views, view models, themes, localization).
src/Bydlo.Desktop     Desktop head (Windows / Linux / macOS).
tests/Bydlo.Tests     Isolated unit tests (no network, no processes).
engines/engines.lock.json   Pinned third-party engine versions + SHA-256 digests.
docs/                 Architecture, build, compatibility, testing, roadmap.
scripts/              Environment + build/check scripts (bash and PowerShell).
.claude/              Agent skills, hooks, settings (see .claude/README.md).
```

Dependency direction: `App → Engines → Platform → Core`; `Core` depends on nothing project-local.
UI never talks to processes directly — only through `ConnectionManager`.

## 2. Environment & files

- Never hardcode absolute paths, drive letters or the repository location. Resolve everything relative to the checkout.
- Temporary files: `<repo>/.tmp/`. Build output stays inside the project (`bin/`, `obj/`, `artifacts/`).
- Before using .NET / NuGet / Python, run `source scripts/env.sh` (bash) or `. ./scripts/env.ps1` (PowerShell).
  They set `TMP`, `TEMP`, `TMPDIR`, `DOTNET_CLI_HOME`, `NUGET_PACKAGES`, NuGet caches, pip cache and `BYDLO_SAFE_DEV=1` for the current shell only.
- Do not install tools globally, change persistent environment variables, write agent memory to the user profile,
  create files in system directories, or delete foreign files/caches without explicit permission.

## 3. Build & check

```bash
source scripts/env.sh
./scripts/check.sh          # restore + build (Release) + tests, never runs engines
```

PowerShell: `. ./scripts/env.ps1; ./scripts/check.ps1`.
Android is not part of v0.1 (planned; see docs/ROADMAP.md).

## 4. Architecture rules

- Every network technology is an `IEngine` adapter registered in `EngineRegistry`. Adding a protocol = new parser + builder/adapter + registry entry + tests + compatibility-matrix row.
- Profiles hold non-secret options only. Secrets (UUIDs, passwords, private keys, raw WG/OVPN configs) live in `ISecretStore`.
- Never log secrets. All engine output passes through `LogRedactor`.
- Privileged operations (TUN, tunnel services, WinDivert, system proxy, DNS, routes) go through `IPrivilegedOperations` /
  `ISystemProxyService` and are guarded by `NetworkSafetyGuard`. They are **disabled by default** and never activated during development.
- Downloaded engines must match the SHA-256 in `engines/engines.lock.json` (`IntegrityVerifier`).
- Do not claim support for a feature on a platform where it is not implemented. Unimplemented paths return
  `EngineAvailability.NotImplemented` and the UI must say so. Keep `docs/COMPATIBILITY.md` truthful.
- Prefer stable upstream implementations (Flowseal Zapret, Flowseal tg-ws-proxy, sing-box, Xray, WireGuard, AmneziaWG, OpenVPN) over rewrites.
- Credit upstream projects in `THIRD_PARTY_NOTICES.md` and in the app (Settings → About). Respect their licenses.

## 5. UI rules

- Beauty and simplicity first: big connect button on Home, advanced options hidden under *Advanced*.
- Themes: Purple (default dark), Light, OLED black, System. Colors live only in `src/Bydlo.App/Themes/*.axaml`.
- All user-visible strings go through localization (`Assets/Strings/en.json`, `ru.json`). Add both languages.
- Desktop: sidebar navigation; narrow windows (and the future Android head): bottom navigation. Same visual language everywhere.

## 5a. Documentation language

- User-facing documentation is written in **Russian**: `README.md`, `THIRD_PARTY_NOTICES.md`, everything in `docs/`,
  and README files inside the repository. Keep names of tools, protocols, commands and code identifiers as they are.
- This file (`CLAUDE.md`) and `.claude/` stay in English (agent configuration). Code comments and commit messages stay in English.
- Screenshots in `docs/images` are rendered with the Russian UI: `dotnet run --project tools/Bydlo.Screenshots -- <dir> ru`.

## 6. Git

- Default branch: `main` for this repository; for future automation never hardcode the branch name or remote URL — detect them.
- The initial version was committed directly as `Initial commit` (no issue, no PR — one-time exception).
- **After the Initial commit**, every independent task follows: issue → dedicated branch → implement → checks →
  PR with `Closes #<issue>`. Create/publish PRs only when the maintainer asks. Never open a PR without an issue.
  Merge only with explicit permission. Check conflicts with the main branch before publishing.
- Detect the hosting: GitHub → `gh`, Gitea → `tea`.
- Branch names describe the task; never use `codex` (any case) in branch names.
- Never push without permission.
- Commit messages: clear, English, imperative.

### No AI contributions in attribution (permanent rule)

- Do not add AI to AUTHORS/CONTRIBUTORS, commit authors/co-authors, maintainers, reviewers or committers.
- No AI `Co-Authored-By` trailers, no agent signatures in commits or PRs.
- Do not change `git config user.name` / `user.email`. Do not rewrite history to hide or fake authorship.
- Do not list AI as a project participant in metadata.

## 7. Agent configuration locations

Only these are allowed: skills `.claude/skills/<name>/SKILL.md`, hooks `.claude/hooks/`,
registration `.claude/settings.json`, inventory `.claude/README.md`.
Never create `.agents`, `.codex`, `.cursor` or similar directories.
Hooks must never start engines, touch the network configuration or do anything risky.
