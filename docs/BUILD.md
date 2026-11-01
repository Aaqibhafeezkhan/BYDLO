# Сборка BYDLO

## Требования

- .NET SDK 10.0.100 или новее в той же линейке (см. `global.json`).
- Git. Bash (на Windows — Git Bash) для скриптов `*.sh` или PowerShell для скриптов `*.ps1`.

## Окружение

Всегда сначала загружайте окружение проекта. Оно держит временные файлы, пакеты NuGet и кэши внутри репозитория
(`.tmp/`) и включает блокировку разработки (`BYDLO_SAFE_DEV=1`), чтобы ни отладочный запуск, ни тест не могли
запустить реальный движок.

```bash
source scripts/env.sh
```

```powershell
. ./scripts/env.ps1
```

## Сборка, тесты, запуск

```bash
bash scripts/check.sh                                  # восстановление + Release-сборка (предупреждения = ошибки) + модульные тесты
dotnet run --project src/Bydlo.Desktop -- --mock       # интерфейс в Mock-режиме
dotnet run --project tools/Bydlo.Screenshots -- .tmp/screenshots ru   # отрисовать все страницы в PNG без окна
```

## Публикация

Самодостаточные однофайловые сборки для каждой платформы:

```bash
dotnet publish src/Bydlo.Desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-x64
```

Чтобы добавить встроенный Zapret в локальный релиз, после публикации выполните упаковщик (только скачивание, SHA-256 и извлечение; ничего не запускает):

```bash
dotnet run --project tools/Bydlo.BundleEngines -c Release -- win-x64 artifacts/win-x64 .
```

Обычные `dotnet build` / `dotnet test` бинарники Zapret не скачивают. Не запускайте упаковщик на рабочей машине, если антивирус блокирует WinDivert: используйте CI. Для Linux/macOS извлекается только целевой nfqws/tpws, без Windows-драйверов из общего upstream-архива. Windows ARM64 получает `ZAPRET-UNAVAILABLE.txt` вместо несовместимого драйвера.

Идентификаторы платформ: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.

## CI

`.github/workflows/ci.yml`:

1. **test** — сборка и модульные тесты на Ubuntu, Windows и macOS (`BYDLO_SAFE_DEV=1`).
2. **desktop** — самодостаточная публикация для всех шести платформ, упаковка закреплённого Zapret с проверкой SHA-256, лицензиями и соответствующими исходниками LGPL-компонентов; артефакты загружаются вместе с `LICENSE` и сведениями о сторонних компонентах.
3. **release** — для тегов `v*` упаковывает артефакты в zip, создаёт `SHA256SUMS.txt` и **черновик** релиза на GitHub.

## Воспроизводимость

- Версия SDK закреплена в `global.json`; версии всех пакетов закреплены централизованно в `Directory.Packages.props`.
- Детерминированная сборка (`Deterministic`); в CI включён `ContinuousIntegrationBuild`.
- BYDLO не собирает движки из исходников; их версии и SHA-256 закреплены в `engines/engines.lock.json`.

## Известные ограничения

- Сборки для macOS не подписаны и не нотаризованы — Gatekeeper покажет предупреждение.
- Сборки для Windows не подписаны.
- Android не входит в v0.1 (см. [ROADMAP.md](ROADMAP.md)).
