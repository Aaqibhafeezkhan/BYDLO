# Подпись кода для Windows

Связано: #215 (установщик), #216 (проверка подписи при автообновлении), #246 (UAC).

## Проблема

Windows показывает BYDLO как приложение «неизвестного издателя»: SmartScreen предупреждает
при запуске, UAC-запрос выглядит подозрительно (особенно вместе с #246).

## Выбранный вариант

**Azure Trusted Signing** — основной вариант:

- недорогая подписка, проверка личности/организации один раз;
- подпись выполняется из CI релиза через OIDC (федеративные credentials GitHub Actions),
  файл `.pfx` и приватный ключ нигде не хранятся;
- встроенная метка времени RFC 3161, отдельный timestamp-сервер не нужен;
- покрывает `BYDLO.exe`, `helper/Bydlo.Helper.exe` и будущий установщик (#215).

Запасной вариант — **SignPath Foundation** (бесплатно для open-source из публичного
репозитория, но с внешней очередью и политикой фонда). Классический OV/EV-сертификат
на токене — дорого и неудобно для CI. Самоподписанный сертификат не убирает
предупреждения SmartScreen и не используется.

## Что подписывается

- `publish/BYDLO-win-x64/BYDLO.exe` и `publish/BYDLO-win-x64/helper/Bydlo.Helper.exe`;
- `publish/BYDLO-win-arm64/BYDLO.exe` и `publish/BYDLO-win-arm64/helper/Bydlo.Helper.exe`;
- будущий установщик из #215 — тем же сертификатом и с той же меткой времени.

Подпись выполняется после `desktop`/`windows-parity` и до упаковки `package`, чтобы
`SHA256SUMS.txt` считался уже по подписанным файлам.

## Секреты

Только секреты CI (Settings → Secrets and variables → Actions), никогда в репозитории:

- `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_SUBSCRIPTION_ID`;
- `AZURE_TRUSTED_SIGNING_ACCOUNT`, `AZURE_TRUSTED_SIGNING_PROFILE`;
- проверку `git grep -ri 'pfx\|p12\|BEGIN PRIVATE'` репозиторий проходит пустым.

Файлов `.pfx`/`.p12`/`.snk` в репозитории нет и быть не должно.

## Проверка релиза

Для тегов `v0.3.1`, `v0.4.0`, `v0.4.1` и `v0.4.2` CI пропускает задание подписания.
В `v0.4.2` BYDLO, помощник и установщики Windows/macOS поставляются без подписи
сертификатом издателя; notarization macOS также пропускается. CI сохраняет проверки
комплектности, SHA-256 и GitHub attestations. Для будущих тегов подпись обязательна.

Репетиция упаковки без подписи: ручной запуск `CI` с `skip_signing=true`.
Он не создаёт тег или черновик релиза. Постоянная подпись отслеживается в #392.

Обязательный шаг релиза (job `sign-windows` в `.github/workflows/ci.yml`):

1. `Azure/trusted-signing-action` подписывает оба `BYDLO.exe` и оба `helper/Bydlo.Helper.exe`;
2. `signtool verify /pa /v` подтверждает подпись каждого файла, неуспех роняет job;
3. `package` проверяет engine-bundle и только потом считает `SHA256SUMS.txt`.

Локально без секретов подпись не ставится. За пределами явных исключений CI без
настроенных credentials останавливает выпуск. Проверка происхождения через attestations
не заменяет системную подпись Windows/macOS.

## Миграция Azure/login v3.1.0 (#428)

В `native-packages` закреплён commit `a641126d1b8aa4d1fa005f4f92df94a3a4c4c906`
релиза [v3.1.0](https://github.com/Azure/login/releases/tag/v3.1.0), а не SHA объекта
аннотированного тега. Главная несовместимость v3 — переход с Node.js 20 на 24.
Задание использует GitHub-hosted `windows-latest`, как и уже обновлённые checkout/setup-dotnet;
для собственного runner нужна поддержка Node.js 24. Azure CLI для OIDC должен быть не ниже 2.30.

Проверены release notes v3.0.0–v3.1.0 и `action.yml`: прежние `client-id`, `tenant-id`,
`subscription-id` совместимы. Используются только secrets, OIDC и Azure Public Cloud
(стандартные `environment=azurecloud`, `audience=api://AzureADTokenExchange`,
`auth-type=SERVICE_PRINCIPAL`). Вход по паролю и дополнительная Az PowerShell-сессия
не включаются. Маскирование client ID и завершающая очистка контекста остаются включёнными
по умолчанию; их отключающие параметры не задаются.

Модель доверия не меняется: `contents: read` и `id-token: write` ограничены заданием;
оно доступно только для тега релиза или ручного запуска, не для PR. Вход Azure дополнительно
ограничен Windows, наличием client ID и обязательной подписью. Федеративные credentials
должны доверять только разрешённым ref/окружениям, но не `pull_request`.

Изолированные тесты проверяют YAML, закрепление версии, inputs, permissions и условия
запуска. Они не выполняют вход Azure. Сопровождающий должен отдельно выполнить разрешённый
подписывающий прогон с настроенными credentials (#392), проверить OIDC, подпись пакетов
и post-cleanup. Успешная реальная подпись этой миграцией не подтверждается.
