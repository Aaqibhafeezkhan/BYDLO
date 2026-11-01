# Сторонние компоненты

BYDLO распространяется по лицензии GPL-3.0-or-later и опирается на работу многих проектов. Сетевые движки — это
**отдельные программы**: BYDLO запускает их как внешние процессы и не линкуется с ними. Когда BYDLO скачивает движок,
он берёт официальный релиз, закреплённый в [`engines/engines.lock.json`](engines/engines.lock.json), и проверяет его
SHA-256. Исходный код каждого движка доступен в указанном ниже репозитории.

## Сетевые движки и инструменты (запускаются как отдельные процессы, в репозитории не хранятся)

| Компонент | Авторы | Лицензия | Исходники | Как использует BYDLO |
|---|---|---|---|---|
| zapret-discord-youtube | Flowseal; на основе zapret от bol-van | MIT | https://github.com/Flowseal/zapret-discord-youtube | Стратегии (`general*.bat`), списки и набор `bin/` используются без изменений; BYDLO разбирает стратегии и запускает `winws.exe` напрямую. |
| zapret (winws, nfqws, tpws) | bol-van | MIT | https://github.com/bol-van/zapret | Движок обхода DPI, входит в релиз Flowseal. |
| WinDivert | basil00 | LGPL-3.0 или GPL-2.0 | https://github.com/basil00/WinDivert | Драйвер перехвата пакетов, используемый `winws.exe` (входит в релиз Flowseal). |
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
