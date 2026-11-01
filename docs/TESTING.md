# Manual testing guide

Automated tests in this repository are **isolated**: they never start an engine, open a tunnel or change system
networking. Everything below must be checked by a human on a machine where losing connectivity is acceptable
(ideally a VM or a spare device). **Do not run these steps on a machine whose remote access depends on its current
network setup.**

Run BYDLO from a shell **without** `BYDLO_SAFE_DEV` (or from a CI build). Keep `logs/bydlo.log` (Settings → Advanced
shows the data folder) for bug reports — it is already scrubbed of credentials, but double-check before sharing.

## 0. Mock Mode (safe everywhere)

1. `dotnet run --project src/Bydlo.Desktop -- --mock`.
2. Connections → *Add demo servers (mock)*. Home → Connect: status goes Connecting → Connected, timer and speed update.
3. Settings → Advanced → Mock scenario = *Fail while connecting*: an error banner appears.
4. Scenario = *Crash after ~20 s*: status becomes Reconnecting, then Connected again (auto-reconnect).
5. Toggle Zapret / TgWsProxy on Home: both show Running independently of the tunnel.
6. Switch themes (Purple / Light / OLED / System) and language (EN / RU); resize the window below 760 px → bottom navigation.

## 1. Engines

- Tools → Engines: install sing-box and Xray. Expect "Ready". Tamper test: edit a digest in `engines/engines.lock.json`,
  rebuild, install → must fail with an integrity error and leave nothing installed.

## 2. Local proxy mode (no admin)

For each protocol you have a server for (VLESS/REALITY, VLESS XHTTP, VMess, Trojan, SS, Hysteria 2, TUIC, SOCKS, WireGuard via sing-box):

1. Import via clipboard, file and QR image.
2. Connect. Configure a browser to SOCKS5 `127.0.0.1:2080`; open an IP-check site; IP must be the server's.
3. Statistics shows live speed (sing-box) or the "no live stats" note (Xray).
4. Disconnect → port 2080 is closed. Kill the engine process manually while connected → reconnect attempts appear.
5. Occupy port 2080 with another program → Connect must refuse with a clear message.

## 3. TUN mode (admin/root)

1. Settings → Restart as administrator (Windows) or run with sudo (Linux/macOS).
2. Home → enable TUN, connect. All apps go through the tunnel; LAN printer / router still reachable (bypass LAN on).
3. With "Keep mesh VPN ranges direct" on, an existing mesh VPN (e.g. Tailscale, ZeroTier, NetBird) keeps working.
4. Disconnect → default route and DNS are back to normal.

## 4. Native WireGuard / AmneziaWG / OpenVPN

1. Install the official client. Import `.conf` / `.ovpn`. Connect as admin.
2. Windows: a tunnel service `WireGuardTunnel$bydlo-wg` (or AmneziaWG equivalent) appears while connected and is removed on disconnect.
3. OpenVPN profiles with `auth-user-pass` without stored credentials must show a clear error (credential editor is on the roadmap).

## 5. Zapret (Windows, admin)

1. Tools → Zapret → *Download pinned release* (or choose an extracted Flowseal folder). Strategies list appears.
2. Make sure no other `winws.exe` / GoodbyeDPI runs (BYDLO refuses to start otherwise).
3. Pick `general`, Start. Logs show winws output; status Running. Check Discord / YouTube.
4. Edit user lists, Save, restart Zapret. Try other strategies and the game filter.
5. Stop → `winws.exe` is gone. Close BYDLO while running → on next start BYDLO offers to stop the leftover process.

## 6. TgWsProxy

1. Tools → TgWsProxy → Install runtime (Python 3.9+ on PATH) or point to an existing `tg-ws-proxy` executable.
2. Start; *Open in Telegram* or copy the link into Telegram Desktop; Telegram connects through the proxy.
3. Regenerate secret → link changes; restart required message shown.
4. Occupy port 1443 → Start must refuse with a port-conflict message.

## 7. System proxy (Windows)

Enable Settings → Advanced → *Set system proxy*, connect in local proxy mode: Windows proxy settings point to 127.0.0.1:2080.
Disconnect (and also close BYDLO while connected) → previous proxy settings are restored.

## Reporting

Include OS + version, BYDLO version, engine and version, protocol, mode (proxy/TUN), and the scrubbed log.
