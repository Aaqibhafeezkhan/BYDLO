# Дорожная карта

## v0.1 — начальный этап разработки

Десктопное приложение (Windows / Linux / macOS), импорт протоколов, адаптеры sing-box / Xray / WireGuard / AmneziaWG / OpenVPN,
интеграция Zapret и TgWsProxy от Flowseal, темы, RU/EN, Mock-режим, сборки в CI. Zapret и TgWsProxy проверены в реальной сети
на Windows x64; процедура ручной проверки описана в [TESTING.md](TESTING.md).

Трей с управлением сессиями, пользовательский автозапуск, подключение при старте и системные уведомления
реализованы. Состояние ручных проверок указано в [матрице совместимости](COMPATIBILITY.md).

## v0.2 — первый публичный релиз на GitHub

- ✅ TgWsProxy без Python: ядро Flowseal tg-ws-proxy перенесено на C# и встроено в BYDLO ([#29](https://github.com/endercodezz/BYDLO/issues/29), этап 2).
- Реализован полный комплект [#29](https://github.com/endercodezz/BYDLO/issues/29): sing-box/Xray, AmneziaWG без клиента на Windows/Linux, OpenVPN с DCO по согласию на Windows и статическим OpenSSL на Unix; фоновые процессы без консоли, исходники и лицензии в комплекте. CI на GitHub собирает все пять платформ, зеркало на Gitea — Linux x64 (#61); Windows на Linux-раннере — #60. Реальные подключения и установку DCO проверяет владелец; автоматические проверки их не запускают.
- ✅ Сопровождающий подтвердил работу поддерживаемых сценариев Windows, включая реальные подключения.
- ✅ TgWsProxy: живые соединения в пуле и автоматический перезапуск слушателя после сна ([#51](https://github.com/endercodezz/BYDLO/issues/51)); проверено на Windows x64.
- TgWsProxy: HTTP/2-мультиплексирование медиа через Cloudflare (`cf_h2` в оригинале).
- Завершить ручное тестирование на Linux и macOS и исправить найденное.
- Редактор профилей (переименование, параметры, учётные данные OpenVPN, выбор движка для профиля).
- Проверка задержки и список серверов с пингом (по запросу пользователя).
- Статистика трафика для Xray (gRPC StatsService).
- Локализованные сообщения об ошибках движков.
- Автоматическое обновление подписок по расписанию.

## v0.3 — десктоп, импорт и маршрутизация

- Обновлён интерфейс рабочего стола ([#133](https://github.com/endercodezz/BYDLO/issues/133)).
- Реализован импорт Clash/Mihomo YAML и поддерживаемого подмножества JSON-конфигураций sing-box и Xray ([#76](https://github.com/endercodezz/BYDLO/issues/76)).
- Реализованы загрузка и проверка наборов правил geosite / geoip ([#71](https://github.com/endercodezz/BYDLO/issues/71)), настройки DNS и группы серверов.
- Реализованы системный прокси для GNOME / KDE и macOS, AmneziaWG для macOS, хранилища секретов Keychain и Secret Service через libsecret.
- Доступна необязательная привилегированная служба-помощник. Статус ручных проверок указан в [матрице совместимости](COMPATIBILITY.md).

## Android — запланировано

- Android ARM64: `VpnService`, интеграция sing-box (libbox), мобильная навигация, сканирование QR камерой и Android Keystore ([#86](https://github.com/endercodezz/BYDLO/issues/86)).

## Позже

- macOS: расширение tpws за пределы IPv4 TCP, подписанные и нотаризованные сборки.
- Автообновление самого BYDLO с проверкой подписи.
- Переход на Avalonia 12.
- Другие языки (переводы от сообщества).

Регистрация Cloudflare WARP и цепочки прокси реализованы (см. [COMPATIBILITY.md](COMPATIBILITY.md));
живую проверку в реальной сети выполняет мейнтейнер.

Zapret для Android (root-first, исследование VpnService) отслеживается в [issue #21](https://github.com/endercodezz/BYDLO/issues/21). Windows ARM64 ожидает совместимый WinDivert driver.
