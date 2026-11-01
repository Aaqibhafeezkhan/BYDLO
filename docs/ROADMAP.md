# Roadmap

## v0.1 — first public version (this release)

Desktop app (Windows / Linux / macOS), protocol import, sing-box / Xray / WireGuard / AmneziaWG / OpenVPN adapters,
Flowseal Zapret and TgWsProxy integration, themes, EN/RU, Mock Mode, CI builds. Real-network verification is pending
(see [TESTING.md](TESTING.md)).

## v0.2 — verification and polish

- Complete the manual test matrix on all desktop platforms; fix findings.
- Profile editor (rename, edit options, OpenVPN credentials, per-profile engine choice).
- Latency test / server ping list (user-triggered).
- Xray live statistics (gRPC StatsService).
- Localized engine error messages.
- Tray icon, start minimized, connect on startup.
- Automatic subscription refresh on a schedule.

## v0.3 — platform depth

- Android ARM64 head: `VpnService` + sing-box (libbox) integration, mobile navigation, QR camera scanning.
- Linux: native zapret (nfqws) integration; system proxy via GNOME / KDE settings.
- macOS: zapret (tpws), system proxy via `networksetup`, AmneziaWG; signed and notarized builds.
- Keychain (macOS), libsecret (Linux), Android Keystore secret stores.
- Optional privileged helper service instead of running the whole app elevated.

## Later

- Cloudflare WARP registration (after license / ToS review).
- Import of full sing-box / Xray JSON configs.
- Rule sets (geosite / geoip) management with verified downloads.
- Auto-update of BYDLO itself with signature verification.
- Avalonia 12 migration.
- More languages (community translations).
