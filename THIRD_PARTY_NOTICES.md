# Сторонние компоненты

BYDLO распространяется по лицензии GPL-3.0-or-later и опирается на работу многих проектов. Сетевые движки — это
**отдельные программы**: BYDLO запускает их как внешние процессы и не линкуется с ними. Когда BYDLO скачивает движок,
он берёт официальный релиз, закреплённый в [`engines/engines.lock.json`](engines/engines.lock.json), и проверяет его
SHA-256. Исходный код каждого движка доступен в указанном ниже репозитории.

## Сетевые движки и инструменты (запускаются как отдельные процессы, в репозитории не хранятся)

| Компонент | Авторы | Лицензия | Исходники | Как использует BYDLO |
|---|---|---|---|---|
| zapret-discord-youtube | Flowseal; на основе zapret от bol-van | MIT | https://github.com/Flowseal/zapret-discord-youtube | Стратегии (`general*.bat`), списки и набор `bin/` используются без изменений; BYDLO разбирает стратегии и запускает `winws.exe` напрямую. |
| zapret (winws, nfqws, tpws) | bol-van | MIT | https://github.com/bol-van/zapret | Windows: внутри Flowseal; Linux/macOS: закреплённый upstream v72.13, nfqws/tpws как отдельные процессы. |
| WinDivert | basil00 | LGPL-3.0 или GPL-2.0 | https://github.com/basil00/WinDivert | Драйвер перехвата пакетов, используемый `winws.exe` (входит в релиз Flowseal). |
| Cygwin 3.4.10 / Newlib | Cygwin contributors / Red Hat и другие | LGPL-3.0-or-later с Cygwin Linking Exception; дополнительные permissive notices Newlib | https://cygwin.com/licensing.html | Неизменённый cygwin1.dll из Flowseal; динамическая библиотека winws. |
| tg-ws-proxy | Flowseal | MIT | https://github.com/Flowseal/tg-ws-proxy | Консольная точка входа `tg-ws-proxy`; по запросу пользователя устанавливается из закреплённого тега в отдельное окружение Python. |
| sing-box | SagerNet / nekohasekai | GPL-3.0-or-later (с дополнительными условиями об использовании названия) | https://github.com/SagerNet/sing-box | Основной движок для VLESS/VMess/Trojan/Shadowsocks/Hysteria 2/TUIC/SOCKS/HTTP/WireGuard; конфигурацию генерирует BYDLO. |
| Xray-core | Project X (XTLS) | MPL-2.0 | https://github.com/XTLS/Xray-core | Движок для транспорта XHTTP и альтернативный движок для протоколов семейства V2Ray. |
| WireGuard for Windows / wireguard-tools | Jason A. Donenfeld, WireGuard LLC | MIT / GPL-2.0 | https://www.wireguard.com | Официальный клиент для нативных туннелей WireGuard (устанавливает пользователь). |
| AmneziaWG (amneziawg-windows, amneziawg-tools) | Amnezia VPN | MIT / GPL-2.0 | https://github.com/amnezia-vpn | Официальный клиент для туннелей AmneziaWG (устанавливает пользователь). |
| OpenVPN | OpenVPN Inc. и участники | GPL-2.0 (с исключением для OpenSSL) | https://github.com/OpenVPN/openvpn | Официальный community-клиент (устанавливает пользователь). |

Лицензия sing-box содержит дополнительные условия, ограничивающие использование названия «sing-box» для производных
работ. BYDLO упоминает sing-box только для указания авторства и не переименовывает и не распространяет изменённые бинарники sing-box.

## Библиотеки, входящие в BYDLO (NuGet)

| Компонент | Лицензия | Исходники |
|---|---|---|
| Avalonia UI (Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter, Avalonia.Skia, Avalonia.Headless) | MIT | https://github.com/AvaloniaUI/Avalonia |
| SkiaSharp / HarfBuzzSharp (через Avalonia) | MIT | https://github.com/mono/SkiaSharp |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| ZXing.Net | Apache-2.0 | https://github.com/micjahn/ZXing.Net |
| System.Security.Cryptography.ProtectedData | MIT | https://github.com/dotnet/runtime |
| xUnit, Microsoft.NET.Test.Sdk (только тесты) | Apache-2.0 / MIT | https://github.com/xunit/xunit |

## Ресурсы

| Ресурс | Лицензия | Исходники |
|---|---|---|
| Шрифт Inter (через Avalonia.Fonts.Inter) | SIL Open Font License 1.1 | https://github.com/rsms/inter |
| Material Design Icons (геометрия иконок в `src/Bydlo.App/Themes/Icons.axaml`) | Apache-2.0 | https://github.com/Templarian/MaterialDesign |
| Тестовые файлы `tests/Bydlo.Tests/Fixtures/general*.bat.txt` (стратегии Flowseal без изменений) | MIT, © 2016-2026 bol-van, © 2024-2026 Flowseal | https://github.com/Flowseal/zapret-discord-youtube |

## Тексты лицензий

- MIT: https://opensource.org/license/mit
- Apache-2.0: https://www.apache.org/licenses/LICENSE-2.0
- MPL-2.0: https://www.mozilla.org/MPL/2.0/
- GPL-2.0: https://www.gnu.org/licenses/old-licenses/gpl-2.0.html
- GPL-3.0: см. [LICENSE](LICENSE)
- LGPL-3.0: https://www.gnu.org/licenses/lgpl-3.0.html
- OFL-1.1: https://openfontlicense.org

Архивы релизов, которые собирает CI, содержат этот файл и `LICENSE`. Если вы распространяете BYDLO вместе с бинарниками
движков, приложите также лицензию каждого движка и предложение предоставить исходный код, если этого требует его лицензия.

## Встроенный Zapret

- Flowseal 1.10.3: MIT, текст из закреплённого [LICENSE.txt](https://github.com/Flowseal/zapret-discord-youtube/blob/1.10.3/LICENSE.txt).
- bol-van/zapret 72.13: MIT, подтверждён по [docs/LICENSE.txt](https://github.com/bol-van/zapret/blob/v72.13/docs/LICENSE.txt), включая copyright bol-van. Универсальный `binaries/mac64/tpws` содержит x86_64 и arm64.
- WinDivert 2.2: выбираем LGPL-3.0; комплект содержит тексты LGPLv3/GPLv3/GPLv2 и закреплённые исходники v2.2.2. Исходный код: https://github.com/basil00/WinDivert/tree/v2.2.2.
- Cygwin DLL 3.4.10: LGPLv3+ и Linking Exception, тексты `CYGWIN_LICENSE`, LGPLv3 и дополнительные notices Newlib включены. Архив `cygwin-3.4.10-1-src.tar.xz` содержит соответствующие исходники и cygport recipe; источник — зеркало дистрибутива Cygwin в ČVUT.

Релизные архивы включают `engines/licenses/` и (для Windows x64) `engines/sources/` с исходниками WinDivert и Cygwin. Их URL и SHA-256 закреплены отдельно в `engines/zapret-sources.lock.json`; упаковка прекращается при несовпадении. GPLv3 находится в корневом `LICENSE`. Бинарники не изменяются; библиотеку Cygwin/WinDivert можно заменить совместимой версией. Нативные пресеты написаны для BYDLO, код/стратегии Sergeydigl3 и других проектов без лицензии не копируются.

## Перенос стандартных проверок Test Zapret

`src/Bydlo.Engines/Zapret/TestZapretProbe.cs` переносит стандартную HTTP/TLS-часть
[Flowseal Test Zapret 1.10.3](https://github.com/Flowseal/zapret-discord-youtube/blob/1.10.3/utils/test%20zapret.ps1)
в управляемый жизненный цикл BYDLO. Изменения: структурированные результаты curl, отдельные
таймауты/неизвестные результаты, проверка HTTP-кода, реальное время успешных ответов,
отмена и запуск только собственных дочерних процессов. Интерактивный координатор, ping
и DPI-suite не переносятся. © 2016–2026 bol-van; © 2024–2026 Flowseal. Лицензия MIT:

```text
MIT License

Copyright (c) 2016-2026 bol-van
Copyright (c) 2024-2026 Flowseal

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
