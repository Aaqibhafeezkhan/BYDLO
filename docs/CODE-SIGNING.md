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

Для тегов `v0.3.1`, `v0.4.0` и `v0.4.1` CI пропускает задание подписания.
В `v0.4.1` BYDLO, помощник и установщики Windows/macOS поставляются без подписи
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
