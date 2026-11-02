# Сторонние компоненты

BYDLO распространяется по лицензии GPL-3.0-or-later и опирается на работу многих проектов.
sing-box, Xray, Zapret, AmneziaWG и OpenVPN работают как **отдельные фоновые программы**;
BYDLO не линкуется с ними. Исключение — перенесённое на C# ядро TgWsProxy, встроенное в BYDLO.
Полный релиз содержит готовые движки либо сборки из закреплённых исходников.
Готовые архивы закреплены в [`engines/engines.lock.json`](engines/engines.lock.json),
входы сборки — в [`engines/bundle-inputs.lock.json`](engines/bundle-inputs.lock.json);
упаковщики проверяют SHA-256 до извлечения. Исходники и лицензии включаются в комплект, как описано ниже.

## Сетевые движки и инструменты (отдельные процессы; бинарники добавляются при упаковке)

| Компонент | Авторы | Лицензия | Исходники | Как использует BYDLO |
|---|---|---|---|---|
| zapret-discord-youtube | Flowseal; на основе zapret от bol-van | MIT | https://github.com/Flowseal/zapret-discord-youtube | Стратегии (`general*.bat`), списки и набор `bin/` используются без изменений; BYDLO разбирает стратегии и запускает `winws.exe` напрямую. |
| zapret (winws, nfqws, tpws) | bol-van | MIT | https://github.com/bol-van/zapret | Windows: внутри Flowseal; Linux/macOS: закреплённый upstream v72.13, nfqws/tpws как отдельные процессы. |
| WinDivert | basil00 | LGPL-3.0 или GPL-2.0 | https://github.com/basil00/WinDivert | Драйвер перехвата пакетов, используемый `winws.exe` (входит в релиз Flowseal). |
| Cygwin 3.4.10 / Newlib | Cygwin contributors / Red Hat и другие | LGPL-3.0-or-later с Cygwin Linking Exception; дополнительные permissive notices Newlib | https://cygwin.com/licensing.html | Неизменённый cygwin1.dll из Flowseal; динамическая библиотека winws. |
| sing-box | SagerNet / nekohasekai | GPL-3.0-or-later (с дополнительными условиями об использовании названия) | https://github.com/SagerNet/sing-box | Основной движок для VLESS/VMess/Trojan/Shadowsocks/Hysteria 2/TUIC/SOCKS/HTTP/WireGuard; конфигурацию генерирует BYDLO. |
| Xray-core | Project X (XTLS) | MPL-2.0 | https://github.com/XTLS/Xray-core | Движок для транспорта XHTTP и альтернативный движок для протоколов семейства V2Ray. |
| Cronet / cronet-go | The Chromium Authors, SagerNet и участники | BSD-3-Clause (Chromium), GPL-3.0-or-later (Go-обвязка) | https://github.com/SagerNet/cronet-go/tree/0d28acc44093df24b2526dea3d6ffefd6b0a54f0 | Неизменённая libcronet из архива sing-box. Текст Chromium BSD включён в `engines/licenses/Chromium.txt`; закреплённый Chromium/NaiveProxy: https://github.com/SagerNet/naiveproxy/tree/72a06c9fca0e2d228588c7f3074bf7efff3ff686. |
| Xray GeoIP / GeoSite | Project X, V2Ray и авторы исходных списков | GPL-3.0; MIT для GeoSite и унаследованных частей | https://github.com/XTLS/Xray-rules-dat | Неизменённые geoip.dat/geosite.dat внутри закреплённого архива Xray; дополнительные тексты лицензий находятся в `engines/licenses/Xray-rules-*.txt`. |
| WireGuard for Windows / wireguard-tools | Jason A. Donenfeld, WireGuard LLC | MIT / GPL-2.0 | https://www.wireguard.com | Официальный клиент для нативных туннелей WireGuard (устанавливает пользователь). |
| AmneziaWG (amneziawg-go 3.1.20260828) | WireGuard LLC, Amnezia VPN и участники | MIT; хост BYDLO — GPL-3.0-or-later | https://github.com/amnezia-vpn/amneziawg-go/tree/v3.1.20260828 | Компилируется вместе с минимальным процесс-хостом, без клиента Amnezia. |
| Wintun 0.14.1 | WireGuard LLC | Prebuilt Binaries License | https://www.wintun.net/ | Неизменённая подписанная DLL для AmneziaWG на Windows; собственная копия также входит в upstream-архив Xray. Текст лицензии: `engines/licenses/Wintun.txt` и `LICENSE-Wintun` рядом с Xray; используется только через разрешённый Wintun API. |
| OpenVPN 2.7.8 | OpenVPN Inc. и участники | GPL-2.0 (с исключениями для OpenSSL/Apache-2.0) | https://github.com/OpenVPN/openvpn/tree/v2.7.8 | Встроенный консольный движок без OpenVPN GUI и служб. |
| OpenVPN DCO 2.8.14 | OpenVPN Inc. и участники | GPL-2.0 | https://github.com/OpenVPN/ovpn-dco-win/tree/2.8.14 | Неизменённые подписанные драйверы Windows 10/11; установка только с явным согласием. |
| OpenSSL 3.6.5 | OpenSSL Project Authors | Apache-2.0 | https://github.com/openssl/openssl/tree/openssl-3.6.5 | Windows: DLL из официального MSI; Unix: статическая библиотека OpenVPN. |
| libcap-ng 0.9.6 | Steve Grubb и участники | LGPL-2.1-or-later | https://github.com/stevegrubb/libcap-ng/tree/v0.9.6 | Статическая библиотека Linux OpenVPN; соответствующие исходники и скрипт пересборки включены. |
| pkcs11-helper / Microsoft Visual C++ runtime | Alon Bar-Lev / Microsoft | BSD (выбранная альтернатива для pkcs11-helper) / условия распространения Microsoft runtime | https://github.com/OpenSC/pkcs11-helper | Неизменённые runtime DLL из официального Windows MSI; тексты OpenVPN bundle сохранены в `engines/licenses/OpenVPN-bundle.txt`. |

Лицензия sing-box содержит дополнительные условия, ограничивающие использование названия «sing-box» для производных
работ. BYDLO упоминает sing-box только для указания авторства и не переименовывает и не распространяет изменённые бинарники sing-box.

В `engines/sources/` релиза включаются закреплённые исходники sing-box, Xray, OpenVPN, OpenSSL,
DCO (Windows), libcap-ng (Linux), а для AmneziaWG — процесс-хост и полные vendored Go-зависимости с их лицензиями.
Входы сборок и SHA-256 находятся в `engines/bundle-inputs.lock.json`; команды — в `docs/BUILD.md`.
Windows MSI используется только как источник файлов: установщик и его custom actions никогда не запускаются сборкой.

## Перенесённый код: tg-ws-proxy

Ядро [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy) v1.11.4 (MIT) перенесено на C# в
`src/Bydlo.Engines/TgWsProxy` и работает внутри BYDLO: протокол MTProto obfuscated2, Fake TLS, пул WebSocket,
резервные пути через Cloudflare и прямой TCP, закодированный список доменов Cloudflare. Python-код в репозиторий не
копируется. Лицензия оригинала:

```text
MIT License

Copyright (c) 2026 Flowseal

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

## Референс протокола: регистрация WARP (код не копируется)

Встроенная регистрация Cloudflare WARP (`src/Bydlo.Core/Warp`) повторяет endpoints
неофициального клиентского API, которые использует [ViRb3/wgcf](https://github.com/ViRb3/wgcf)
(MIT, © ViRb3). Код wgcf в BYDLO не копируется: HTTP-клиент и фабрика WireGuard-профиля
написаны заново, арифметика X25519 реализована самостоятельно по RFC 7748
(сверена с тестовыми векторами RFC). BYDLO не аффилирован ни с Cloudflare, ни с wgcf.

```text
MIT License

Copyright (c) 2020 ViRb3

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
| Списки подписок [igareck/vpn-configs-for-russia](https://github.com/igareck/vpn-configs-for-russia) (скачиваются приложением с зеркал автора, в проект не копируются) | GPL-3.0 | https://github.com/igareck/vpn-configs-for-russia |

## Тексты лицензий

- MIT: https://opensource.org/license/mit
- Apache-2.0: https://www.apache.org/licenses/LICENSE-2.0
- MPL-2.0: https://www.mozilla.org/MPL/2.0/
- GPL-2.0: https://www.gnu.org/licenses/old-licenses/gpl-2.0.html
- GPL-3.0: см. [LICENSE](LICENSE)
- LGPL-3.0: https://www.gnu.org/licenses/lgpl-3.0.html
- OFL-1.1: https://openfontlicense.org

Полный комплект содержит этот файл, `LICENSE`, `engines/licenses/`, `engines/sources/`, lock-файлы
и `ENGINES-SHA256SUMS.txt`. Сохраняйте их при переупаковке и распространении; не ограничивайтесь
исполняемым файлом BYDLO. Команды сборки, состав исходников и порядок обновления описаны
в [docs/BUILD.md](docs/BUILD.md#обновление-встроенных-компонентов). CI на GitHub создаёт комплект
для всех пяти платформ и публикует черновики релизов; зеркало на Gitea собирает только Linux x64.
macOS поддерживается только на Apple Silicon (`osx-arm64`). Универсальный upstream-бинарник
tpws сохраняется без изменений; наличие в нём x86_64 не означает поддержку Intel Mac в BYDLO.

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
