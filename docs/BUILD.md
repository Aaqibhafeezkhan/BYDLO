# Building BYDLO

## Requirements

- .NET SDK 10.0.100 or newer feature band (see `global.json`).
- Git. Bash (Git Bash on Windows) for the `*.sh` scripts, or PowerShell for the `*.ps1` scripts.

## Environment

Always load the project environment first. It keeps temporary files, NuGet packages and caches inside the checkout
(`.tmp/`) and enables the development safety lock (`BYDLO_SAFE_DEV=1`), so that no real engine can be started by a
debug run or test.

```bash
source scripts/env.sh
```

```powershell
. ./scripts/env.ps1
```

## Build, test, run

```bash
bash scripts/check.sh                                  # restore + Release build (warnings as errors) + unit tests
dotnet run --project src/Bydlo.Desktop -- --mock       # start the UI in Mock Mode
dotnet run --project tools/Bydlo.Screenshots -- .tmp/screenshots en   # render every page to PNG (headless)
```

## Publishing

Self-contained single-file builds per runtime:

```bash
dotnet publish src/Bydlo.Desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-x64
```

Runtime identifiers: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.

## CI

`.github/workflows/ci.yml`:

1. **test** — build + unit tests on Ubuntu, Windows and macOS (`BYDLO_SAFE_DEV=1`).
2. **desktop** — self-contained publish for all six runtimes, uploaded as artifacts (with `LICENSE` and notices).
3. **release** — on `v*` tags, zips the artifacts, writes `SHA256SUMS.txt` and creates a **draft** GitHub release.

## Reproducibility

- SDK pinned by `global.json`; all package versions pinned centrally in `Directory.Packages.props`.
- `Deterministic` builds; `ContinuousIntegrationBuild` in CI.
- Engines are not built from source by BYDLO; their versions and SHA-256 digests are pinned in `engines/engines.lock.json`.

## Known limitations

- macOS builds are not signed or notarized; Gatekeeper will warn.
- Windows builds are not code-signed.
- Android is not part of v0.1 (see [ROADMAP.md](ROADMAP.md)).
