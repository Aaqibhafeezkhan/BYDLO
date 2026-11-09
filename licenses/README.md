# Лицензии библиотек и ресурсов приложения

BYDLO остаётся под GPL-3.0-or-later: основной текст — `LICENSE` рядом с приложением.
Лицензии сторонних компонентов не заменяют лицензию BYDLO.

Оригинальные тексты ниже распространяются без перевода и изменения байтов.
`manifest.json` содержит версии проверенных компонентов, источники и SHA-256 каждого текста.
Версии библиотек сверены с `Directory.Packages.props` и восстановленным графом NuGet;
транзитивные компоненты могут присутствовать только на отдельных платформах.

- `Avalonia.txt` — семейство Avalonia 11.3.22, MIT.
- `Avalonia.BuildServices.txt` — Avalonia.BuildServices 11.3.2, MIT; только инструмент сборки.
- `ANGLE.txt` — Avalonia.Angle.Windows.Natives 2.1.25547.20250602, BSD-3-Clause.
- `SkiaSharp-HarfBuzzSharp.txt` и `SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt` —
  SkiaSharp 2.88.9, HarfBuzzSharp 8.3.1.1 и их native assets. Тексты в пакетах Windows,
  Linux и macOS совпадают. Помимо MIT обвязок сохранены условия встроенных native-компонентов.
- `CommunityToolkit.Mvvm.txt`, `CommunityToolkit-THIRD-PARTY-NOTICES.txt` —
  CommunityToolkit.Mvvm 8.4.2, MIT и оригинальные дополнительные уведомления пакета.
- `YamlDotNet.txt` — YamlDotNet 18.1.0, MIT.
- `ZXing.Net.txt` — ZXing.Net 0.16.11, Apache-2.0.
- `gRPC.txt` — Grpc.Net.Client, Grpc.Net.Common и Grpc.Core.Api 2.85.0, Apache-2.0.
- `Protobuf.txt` — Google.Protobuf 3.36.2, BSD-3-Clause.
- `MicroCom.txt` — MicroCom.Runtime 0.11.0, MIT.
- `Tmds.DBus.txt` — Tmds.DBus.Protocol 0.21.3, MIT.
- `DotNet.txt`, `DotNet-THIRD-PARTY-NOTICES.txt` — .NET 10.0.14, в том числе
  System.Security.Cryptography.ProtectedData и System.ServiceProcess.ServiceController.
- `DotNet-Extensions-THIRD-PARTY-NOTICES.txt` — дополнительные уведомления транзитивных
  Microsoft.Extensions.DependencyInjection.Abstractions / Logging.Abstractions 8.0.1.
- `Inter.txt` — SIL OFL 1.1 для Inter 3.019 из Avalonia.Fonts.Inter 11.3.22.
  Версия подтверждена таблицей `name` исходного Inter-Regular.ttf из закреплённого Avalonia.
  MIT пакета Avalonia не заменяет лицензию самого шрифта.
- `MaterialDesignIcons.txt` — уведомление Pictogrammers для использованных геометрий;
  текст Apache-2.0 также включён в `gRPC.txt` и `ZXing.Net.txt`.
- `Velopack.txt` — Velopack 1.2.161, MIT. Для совместимости тот же текст сохраняется
  рядом с приложением как `LICENSE-Velopack.txt`.

Полные upstream-уведомления иногда перечисляют компоненты, не используемые данным
RID или прошедшие trimming. Сохранение этих уведомлений не означает использование всех
перечисленных возможностей. xUnit, coverlet, Microsoft.NET.Test.Sdk, Grpc.Tools и другие
инструменты тестов/сборки не входят в исполняемое приложение.

При обновлении зависимости сверяйте её `.nuspec`, лицензию и дополнительные notices
из точного пакета либо исходной ревизии. Обновляйте исходный текст, источник, версию,
SHA-256 в `manifest.json`, сведения в `THIRD_PARTY_NOTICES.md` и «О программе».
Оригинальные notices не переводятся. `.gitattributes` сохраняет байты `*.txt`, включая
окончания строк. `dotnet publish` копирует весь каталог; проверка релизного комплекта
отклоняет пропавшие или изменённые тексты до упаковки и после извлечения update-пакета.

Лицензии движков и их зависимостей находятся отдельно в `engines/licenses/`, а соответствующие
исходники — в `engines/sources/` полного релиза. Общий список: `THIRD_PARTY_NOTICES.md`.
