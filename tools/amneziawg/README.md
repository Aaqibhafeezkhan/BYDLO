# Хост AmneziaWG

Этот исполняемый файл управляет жизненным циклом закреплённого `amneziawg-go`.
Криптография и протокол импортируются из upstream; конфигурация, адреса, маршруты,
DNS и журнал восстановления управляются BYDLO. GUI и службы Amnezia не нужны.

`go.mod` и `go.sum` фиксируют исходники и зависимости. Go 1.27.3 используется в CI.
Сборка выполняется через `scripts/build-bundled-engines.ps1`, без запуска движка.
Пример команды компиляции из этого каталога:

```text
CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -mod=readonly -trimpath -buildvcs=false -o bydlo-amneziawg .
```

Параметры запуска предназначены только для адаптера BYDLO: путь к защищённому JSON
и PID родителя. Хост завершается при закрытии stdin или смерти родителя; на Unix
также обрабатывает SIGTERM. macOS пока не поддерживается: нужен отдельный жизненный
цикл utun и восстановления сетевых настроек.

В релиз включается архив исходников с `vendor/` и лицензиями зависимостей.
Лицензия `amneziawg-go` — MIT, copyright WireGuard LLC и Amnezia VPN;
исходники: https://github.com/amnezia-vpn/amneziawg-go/tree/v3.1.20260828.
