<p align="center"><img src="src/Bydlo.App/Assets/bydlo-256.png" width="96" alt="BYDLO logo"></p>

<h1 align="center">BYDLO</h1>
<p align="center"><b>B</b>ypass <b>Y</b>our <b>D</b>igital <b>L</b>imitations <b>O</b>nline<br>
<i>One app. Every protocol. Zero bullshit.</i></p>

<p align="center"><img src="docs/images/home-connected.png" width="760" alt="BYDLO home screen"></p>

BYDLO is a free, open-source GUI launcher for VPNs, proxies and censorship-bypass tools.
Import a link, a config file, a QR code or a subscription — and connect with one button.
BYDLO does not reinvent protocols: it drives proven upstream engines (sing-box, Xray-core, WireGuard,
AmneziaWG, OpenVPN) and Flowseal's [Zapret](https://github.com/Flowseal/zapret-discord-youtube) and
[tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy) behind one clean interface.

> **Status: v0.1 — first public version.** The application builds, its logic is covered by isolated unit tests
> and the UI can be tried in Mock Mode. Real tunnels have **not** been verified by the authors on every platform yet —
> see [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) and [docs/TESTING.md](docs/TESTING.md).

## Features

- **One-button connect** with a big, animated connect control, live speed and session timer.
- **Protocols:** VLESS (incl. REALITY, XHTTP via Xray), VMess, Trojan, Shadowsocks, Hysteria 2, TUIC, SOCKS5, HTTP(S),
  WireGuard, AmneziaWG, OpenVPN.
- **Import:** share links (`vless://`, `vmess://`, `trojan://`, `ss://`, `hy2://`, `tuic://`, `socks5://`, `wireguard://`…),
  base64 subscriptions, `.conf` (WireGuard / AmneziaWG), `.ovpn`, QR images.
- **Zapret (Windows):** uses Flowseal strategies unchanged, runs `winws.exe` headless, strategy picker, game filter,
  user lists editor, logs, pinned + SHA-256-verified downloads.
- **TgWsProxy:** headless Flowseal tg-ws-proxy, one-click `tg://proxy` link, secret management, port-conflict checks.
- **Routing:** local proxy or full-device TUN, split tunneling, DNS, direct / tunnel / block lists, mesh-VPN ranges kept direct.
- **Safety:** secrets encrypted at rest (DPAPI on Windows), credentials scrubbed from logs, crash recovery for orphaned engines,
  verified engine downloads, privileged operations isolated and off by default.
- **Design:** purple (default), light, OLED-black and system themes; English and Russian; adaptive layout from phone width to desktop.
- **Mock Mode:** a clearly labelled simulation of connect / traffic / errors for UI testing without any network engine.

| | | |
|---|---|---|
| ![Light](docs/images/home-light.png) | ![OLED](docs/images/home-oled.png) | ![Tools](docs/images/tools.png) |
| ![Connections](docs/images/connections.png) | ![Statistics](docs/images/statistics.png) | ![Settings](docs/images/settings.png) |

## Platforms

| Platform | v0.1 |
|---|---|
| Windows 10/11 x64, ARM64 | Builds in CI; main development target |
| Linux x64, ARM64 | Builds in CI; Zapret not yet |
| macOS Intel, Apple Silicon | Builds in CI (unsigned); Zapret not yet |
| Android ARM64 | Planned (see [roadmap](docs/ROADMAP.md)) |

Details per protocol and platform: [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md).

## Quick start (from source)

```bash
git clone https://github.com/endercodezz/BYDLO.git
cd BYDLO
source scripts/env.sh          # PowerShell: . ./scripts/env.ps1
dotnet run --project src/Bydlo.Desktop -- --mock   # UI in Mock Mode
```

`scripts/env.*` keep all caches inside the checkout and enable the development safety lock (`BYDLO_SAFE_DEV=1`),
so real engines refuse to start. To use BYDLO for real, run it from a normal shell without that variable, or use a
build from CI / Releases. More in [docs/BUILD.md](docs/BUILD.md).

## Documentation

- [Architecture](docs/ARCHITECTURE.md)
- [Build instructions](docs/BUILD.md)
- [Protocol × platform compatibility](docs/COMPATIBILITY.md)
- [Manual testing guide](docs/TESTING.md)
- [Roadmap](docs/ROADMAP.md)
- [Third-party notices and licenses](THIRD_PARTY_NOTICES.md)
- [Contributor / agent rules](CLAUDE.md)

## Credits

BYDLO stands on the shoulders of:
[Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube),
[bol-van/zapret](https://github.com/bol-van/zapret),
[Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy),
[SagerNet/sing-box](https://github.com/SagerNet/sing-box),
[XTLS/Xray-core](https://github.com/XTLS/Xray-core),
[WireGuard](https://www.wireguard.com),
[AmneziaWG](https://github.com/amnezia-vpn),
[OpenVPN](https://github.com/OpenVPN/openvpn),
[Avalonia UI](https://github.com/AvaloniaUI/Avalonia) and others listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
Thank you.

## License

BYDLO is licensed under the [GNU General Public License v3.0 or later](LICENSE).
Third-party components keep their own licenses — see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Use BYDLO in accordance with the laws of your country. It is a client for networks and servers you are authorized to use.
