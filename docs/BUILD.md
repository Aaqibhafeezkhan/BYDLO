# Сборка BYDLO

## Требования

- .NET SDK 10.0.100 или новее в той же линейке (см. `global.json`).
- Git. Bash (на Windows — Git Bash) для скриптов `*.sh` или PowerShell для скриптов `*.ps1`.

## Обычный запуск с реальными движками

Для сборки нужны Git и .NET SDK 10.0.100 или новее в линейке 10.0 (см. `global.json`),
а также PowerShell или bash. Установите зависимости, клонируйте репозиторий и перейдите в его каталог.
На Linux нужны системные зависимости Avalonia (X11 и fontconfig).

**PowerShell — обычный пользовательский запуск:**

```powershell
git clone https://github.com/endercodezz/BYDLO.git
cd BYDLO
# Убрать флаги разработки только из текущей оболочки.
Remove-Item Env:BYDLO_MOCK, Env:BYDLO_SAFE_DEV, Env:BYDLO_DATA_DIR -ErrorAction SilentlyContinue
dotnet restore BYDLO.slnx
dotnet build BYDLO.slnx -c Release --no-restore
dotnet run --project src/Bydlo.Desktop -c Release --no-build
```

**bash — обычный пользовательский запуск:**

```bash
git clone https://github.com/endercodezz/BYDLO.git
cd BYDLO
unset BYDLO_MOCK BYDLO_SAFE_DEV BYDLO_DATA_DIR
dotnet restore BYDLO.slnx
dotnet build BYDLO.slnx -c Release --no-restore
dotnet run --project src/Bydlo.Desktop -c Release --no-build
```

В существующем checkout начните с очистки переменных. Для обычного запуска не загружайте `scripts/env.*`:
это окружение агента, включающее защиту от запуска реальных движков. Команды очистки выше предназначены
для ручного запуска сопровождающим; агент не выполняет их для проверки подключения.

Без явного выбора mock приложение использует реальные адаптеры. Импортируйте профиль, настройте нужный
движок в интерфейсе и подключитесь вручную. Возможности и необходимые права зависят от платформы:
см. [совместимость](COMPATIBILITY.md). Сама сборка не проверяет реальное подключение.

- `--mock` и `BYDLO_MOCK=1` принудительно включают имитацию на текущий сеанс; отсутствие аргумента
  не отменяет переменную. Их значение не сохраняется как пользовательский выбор.
- Сохранённый переключатель «Настройки → Дополнительно → Mock-режим» тоже включает имитацию.
  Для реального подключения выключите его после удаления флагов; по умолчанию он выключен.
- `BYDLO_SAFE_DEV=1` блокирует реальные движки и привилегированные операции, но сам по себе не включает mock.
- `BYDLO_DATA_DIR` выбирает каталог данных; `scripts/env.*` задают `.tmp/appdata`.
  После удаления переменной используется обычный каталог данных пользователя с собственными сохранёнными настройками.
  Если нужен именно прежний каталог, сохраните эту переменную и проверьте в нём переключатель mock.

## Безопасная работа нейросетевого агента

Этот сценарий отделён от пользовательского запуска. Агент всегда сохраняет блокировку сети,
а для проверки интерфейса явно включает mock. Реальное подключение проверяет сопровождающий вручную.

```powershell
. ./scripts/env.ps1
./scripts/check.ps1
dotnet run --project src/Bydlo.Desktop -- --mock
```

```bash
source scripts/env.sh
bash scripts/check.sh
dotnet run --project src/Bydlo.Desktop -- --mock
```

`check.*` выполняют восстановление зависимостей, Release-сборку с предупреждениями как ошибками
и изолированные тесты с поддельными процессами и HTTP-обработчиками. Реальные движки не запускаются.
`env.*` держат кэши и временные файлы в `.tmp/`, задают `BYDLO_SAFE_DEV=1` и отдельный каталог данных.
Mock — инструмент агентской разработки, а не штатный способ пользовательского запуска.

## Публикация

Самодостаточные однофайловые сборки для каждой платформы:

```bash
dotnet publish src/Bydlo.Desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-x64
```

Чтобы добавить sing-box, Xray и Zapret в локальный релиз, после публикации выполните упаковщик (только скачивание, SHA-256 и извлечение; движки не запускаются):

```bash
dotnet run --project tools/Bydlo.BundleEngines -c Release -- win-x64 artifacts/win-x64 .
```

Затем соберите и добавьте AmneziaWG/OpenVPN. Нужен Go 1.27.3 и PowerShell 7; для Linux/macOS также
компилятор C, make, Perl, pkg-config, а на Linux — autoconf/automake/libtool. Unix-сборка выполняется
на машине той же архитектуры. Windows MSI извлекается read-only через закреплённый lessmsi;
его действия установки, GUI, движки, драйверы и службы не запускаются.

```powershell
. ./scripts/env.ps1
./scripts/build-bundled-engines.ps1 -Rid win-x64 -PublishDirectory artifacts/win-x64
```

Скрипт проверяет SHA-256 входов из `engines/bundle-inputs.lock.json`, Go использует `go.mod`/`go.sum`
и `-mod=readonly`. В архив включаются исходники, лицензии и `ENGINES-SHA256SUMS.txt`.
В каталоге релиза AmneziaWG находится в `engines/amneziawg/<version>/`, OpenVPN — в
`engines/openvpn/<version>/`. Обычная сборка приложения не требует Go и бинарников движков.

Обычные `dotnet build` / `dotnet test` бинарники Zapret не скачивают. Не запускайте упаковщик на рабочей машине, если антивирус блокирует WinDivert: используйте CI. Для Linux/macOS извлекается только целевой nfqws/tpws, без Windows-драйверов из общего upstream-архива. Windows ARM64 получает `ZAPRET-UNAVAILABLE.txt` вместо несовместимого драйвера.

Идентификаторы платформ: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.

## CI

`.github/workflows/ci.yml`:

1. **test** — сборка и модульные тесты на Ubuntu, Windows и macOS (`BYDLO_SAFE_DEV=1`).
2. **desktop** — самодостаточная публикация для шести платформ, упаковка закреплённых sing-box/Xray/Zapret, сборка AmneziaWG для Windows/Linux и OpenVPN для всех платформ; лицензии, соответствующие исходники и хеши. Сетевые движки не запускаются. Для Unix используются нативные x64/ARM64 runners.
3. **release** — для тегов `v*` упаковывает артефакты в zip, создаёт `SHA256SUMS.txt` и **черновик** релиза на GitHub.

## Воспроизводимость

- Версия SDK закреплена в `global.json`; версии всех пакетов закреплены централизованно в `Directory.Packages.props`.
- Детерминированная сборка (`Deterministic`); в CI включён `ContinuousIntegrationBuild`.
- Бинарные релизы закреплены в `engines/engines.lock.json`; входы сборок — в `engines/bundle-inputs.lock.json`, Go-зависимости — в `tools/amneziawg/go.mod` и `go.sum`.
- Обновление: измените версию и URL входа, скачайте архив отдельно, проверьте upstream checksum/подпись и замените SHA-256. Для AmneziaWG обновите Go-модуль и суммы. Запустите обе команды упаковки для всех затронутых платформ, не запуская движки; изменения версий, лицензий и документации отправляйте одним PR.

## Известные ограничения

- Сборки для macOS не подписаны и не нотаризованы — Gatekeeper покажет предупреждение.
- Сборки для Windows не подписаны.
- Встроенный OpenVPN для Linux собирается на Ubuntu 24.04; совместимость со старыми версиями glibc не заявляется.
- Android не входит в v0.1 (см. [ROADMAP.md](ROADMAP.md)).
