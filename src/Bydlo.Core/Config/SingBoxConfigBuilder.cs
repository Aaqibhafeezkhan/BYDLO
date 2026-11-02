using System.Text.Json;
using System.Text.Json.Nodes;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Import.Parsers;
using Bydlo.Core.Models;

namespace Bydlo.Core.Config;

/// <summary>Options that are not part of the profile itself.</summary>
public sealed record BuildContext(AppSettings Settings, int? ClashApiPort = null, string? ClashApiSecret = null, LocalProxyAuth? LocalProxyAuth = null);

/// <summary>
/// Generates sing-box configuration (schema of sing-box 1.12+, verified against the 1.14 migration guide):
/// new DNS server format, rule actions instead of special outbounds, WireGuard as an endpoint.
/// </summary>
public sealed class SingBoxConfigBuilder
{
    public const string ProxyTag = "proxy";

    public static readonly IReadOnlySet<ProtocolKind> Supported = new HashSet<ProtocolKind>
    {
        ProtocolKind.Vless, ProtocolKind.Vmess, ProtocolKind.Trojan, ProtocolKind.Shadowsocks,
        ProtocolKind.Hysteria2, ProtocolKind.Tuic, ProtocolKind.Socks5, ProtocolKind.Http, ProtocolKind.WireGuard,
    };

    /// <summary>CGNAT / mesh-VPN ranges kept outside the tunnel when <see cref="RoutingSettings.PreserveMeshVpnRanges"/> is on.</summary>
    public static readonly string[] MeshVpnRanges = ["100.64.0.0/10", "fd7a:115c:a1e3::/48"];

    public string BuildJson(Profile profile, ProfileSecrets secrets, BuildContext ctx) =>
        Build(profile, secrets, ctx).ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Builds a relay chain: traffic enters at <c>hops[0]</c> and leaves at the last hop.
    /// Every hop dials through the previous one via sing-box <c>detour</c>
    /// (WireGuard hops become endpoints with <c>detour</c>). Route and DNS point at the exit hop.
    /// </summary>
    public string BuildChainJson(IReadOnlyList<ChainHop> hops, BuildContext ctx) =>
        BuildChain(hops, ctx).ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public JsonObject BuildChain(IReadOnlyList<ChainHop> hops, BuildContext ctx)
    {
        if (hops.Count < ChainValidator.MinHops)
        {
            throw new Engines.EngineException("A chain needs at least two servers.", localizationKey: "error.chain.toofew");
        }

        if (hops.Count > ChainValidator.MaxHops)
        {
            throw new Engines.EngineException("This chain has too many hops.", localizationKey: "error.chain.toolong");
        }

        foreach (var hop in hops)
        {
            if (!Supported.Contains(hop.Profile.Protocol))
            {
                throw new Engines.EngineException($"The {hop.Profile.Protocol} hop cannot be chained by sing-box.", localizationKey: "error.chain.engine");
            }
        }

        var settings = ctx.Settings;
        var routing = settings.Routing;
        var exitTag = Tag(hops.Count - 1);
        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = true },
            ["dns"] = BuildDns(routing, exitTag),
            ["inbounds"] = new JsonArray(BuildInbound(settings, ctx.LocalProxyAuth)),
        };

        var outbounds = new JsonArray();
        JsonArray? endpoints = null;
        for (var i = 0; i < hops.Count; i++)
        {
            var tag = Tag(i);
            var (profile, secrets) = (hops[i].Profile, hops[i].Secrets);
            var via = i > 0 ? Tag(i - 1) : null;
            if (profile.Protocol == ProtocolKind.WireGuard)
            {
                endpoints ??= new JsonArray();
                var ep = BuildWireGuardEndpoint(profile, secrets, tag);
                if (via is not null)
                {
                    ep["detour"] = via;
                }

                endpoints.Add(ep);
            }
            else
            {
                var o = BuildOutbound(profile, secrets, tag);
                if (via is not null)
                {
                    o["detour"] = via;
                }

                outbounds.Add(o);
            }
        }

        if (endpoints is not null)
        {
            root["endpoints"] = endpoints;
        }

        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
        root["outbounds"] = outbounds;
        root["route"] = BuildRoute(routing, settings.Mode, exitTag);

        if (ctx.ClashApiPort is { } apiPort)
        {
            root["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{apiPort}",
                    ["secret"] = ctx.ClashApiSecret ?? "",
                },
            };
        }

        return root;
    }

    internal static string Tag(int index) => $"proxy-{index}";

    public JsonObject Build(Profile profile, ProfileSecrets secrets, BuildContext ctx)
    {
        if (!Supported.Contains(profile.Protocol))
        {
            throw new NotSupportedException($"sing-box adapter does not handle {profile.Protocol}.");
        }

        var settings = ctx.Settings;
        var routing = settings.Routing;
        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = true },
            ["dns"] = BuildDns(routing),
            ["inbounds"] = new JsonArray(BuildInbound(settings, ctx.LocalProxyAuth)),
        };

        var outbounds = new JsonArray();
        if (profile.Protocol == ProtocolKind.WireGuard)
        {
            root["endpoints"] = new JsonArray(BuildWireGuardEndpoint(profile, secrets));
        }
        else
        {
            outbounds.Add(BuildOutbound(profile, secrets));
        }

        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
        root["outbounds"] = outbounds;
        root["route"] = BuildRoute(routing, settings.Mode);

        if (ctx.ClashApiPort is { } apiPort)
        {
            root["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{apiPort}",
                    ["secret"] = ctx.ClashApiSecret ?? "",
                },
            };
        }

        return root;
    }

    /// <summary>A retained TUN with no Internet-direct fallback. Transport lives in another process.</summary>
    public JsonObject BuildKillSwitch(AppSettings settings, int backendPort)
    {
        if (settings.Mode != ConnectionMode.Tun || !settings.KillSwitch)
        {
            throw new InvalidOperationException("Kill switch requires TUN mode and explicit opt-in.");
        }

        // Only LAN and mesh exceptions survive. Split routing cannot silently weaken protection.
        var routing = new RoutingSettings
        {
            BypassPrivateNetworks = settings.Routing.BypassPrivateNetworks,
            PreserveMeshVpnRanges = settings.Routing.PreserveMeshVpnRanges,
            RemoteDns = settings.Routing.RemoteDns,
            BlockDomains = [.. settings.Routing.BlockDomains],
        };
        var tunSettings = new AppSettings { Mode = ConnectionMode.Tun, Routing = routing };
        var inbound = BuildInbound(tunSettings);
        var exclusions = inbound["route_exclude_address"]?.AsArray() ?? new JsonArray();
        if (routing.BypassPrivateNetworks)
        {
            foreach (var cidr in PrivateNetworkRanges) { exclusions.Add(cidr); }
        }
        if (exclusions.Parent is null && exclusions.Count > 0) { inbound["route_exclude_address"] = exclusions; }

        // ip_is_private includes ULA/CGNAT: use exact ranges so LAN and mesh remain independent.
        var route = BuildRoute(routing, ConnectionMode.Tun);
        foreach (var rule in route["rules"]!.AsArray().OfType<JsonObject>())
        {
            if (rule.Remove("ip_is_private")) { rule["ip_cidr"] = ToArray(PrivateNetworkRanges); }
        }
        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = true },
            ["dns"] = BuildDns(routing),
            ["inbounds"] = new JsonArray(inbound),
            ["outbounds"] = new JsonArray(
                new JsonObject { ["type"] = "socks", ["tag"] = ProxyTag, ["server"] = "127.0.0.1", ["server_port"] = backendPort, ["version"] = "5" },
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
            ["route"] = route,
        };
    }

    // Deliberately exclude Tailscale ULA from the LAN exception.
    public static readonly string[] PrivateNetworkRanges =
        [
            "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",
            "169.254.0.0/16", "fe80::/10", "fc00::/8",
            "fd80::/9", "fd00::/10", "fd40::/11",
            "fd60::/12", "fd70::/13", "fd7c::/14",
            "fd78::/15", "fd7b::/16", "fd7a:8000::/17",
            "fd7a:4000::/18", "fd7a:2000::/19", "fd7a::/20",
            "fd7a:1800::/21", "fd7a:1400::/22", "fd7a:1200::/23",
            "fd7a:1000::/24", "fd7a:1180::/25", "fd7a:1100::/26",
            "fd7a:1160::/27", "fd7a:1140::/28", "fd7a:1150::/29",
            "fd7a:1158::/30", "fd7a:115e::/31", "fd7a:115d::/32",
            "fd7a:115c::/33", "fd7a:115c:c000::/34", "fd7a:115c:8000::/35",
            "fd7a:115c:b000::/36", "fd7a:115c:a800::/37", "fd7a:115c:a400::/38",
            "fd7a:115c:a200::/39", "fd7a:115c:a000::/40", "fd7a:115c:a100::/41",
            "fd7a:115c:a180::/42", "fd7a:115c:a1c0::/43", "fd7a:115c:a1f0::/44",
            "fd7a:115c:a1e8::/45", "fd7a:115c:a1e4::/46", "fd7a:115c:a1e0::/47",
            "fd7a:115c:a1e2::/48",
        ];

    private static JsonObject BuildDns(RoutingSettings routing, string proxyTag = ProxyTag)
    {
        var servers = new JsonArray
        {
            new JsonObject { ["type"] = "https", ["tag"] = "remote", ["server"] = routing.RemoteDns, ["detour"] = proxyTag },
        };
        servers.Add(routing.DirectDns is "local" or ""
            ? new JsonObject { ["type"] = "local", ["tag"] = "local" }
            : new JsonObject { ["type"] = "udp", ["tag"] = "local", ["server"] = routing.DirectDns });

        var rules = new JsonArray();
        if (routing.DirectDomains.Count > 0)
        {
            rules.Add(new JsonObject { ["domain_suffix"] = ToArray(routing.DirectDomains), ["server"] = "local" });
        }

        return new JsonObject { ["servers"] = servers, ["rules"] = rules, ["final"] = "remote" };
    }

    private static JsonObject BuildInbound(AppSettings settings, LocalProxyAuth? lanAuth = null)
    {
        if (settings.Mode == ConnectionMode.Tun)
        {
            var tun = new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["interface_name"] = "bydlo-tun",
                ["address"] = new JsonArray("172.19.0.1/30", "fdfe:dcba:9876::1/126"),
                ["auto_route"] = true,
                ["strict_route"] = true,
            };
            var exclude = new JsonArray();
            if (settings.Routing.PreserveMeshVpnRanges)
            {
                foreach (var r in MeshVpnRanges)
                {
                    exclude.Add(r);
                }
            }

            foreach (var c in settings.Routing.DirectCidrs)
            {
                exclude.Add(c);
            }

            if (exclude.Count > 0)
            {
                tun["route_exclude_address"] = exclude;
            }

            return tun;
        }

        var inbound = new JsonObject
        {
            ["type"] = "mixed",
            ["tag"] = "mixed-in",
            ["listen"] = lanAuth is null ? "127.0.0.1" : "0.0.0.0",
            ["listen_port"] = settings.LocalProxyPort,
        };
        if (lanAuth is not null)
        {
            inbound["users"] = new JsonArray(new JsonObject { ["username"] = lanAuth.Username, ["password"] = lanAuth.Password });
        }

        return inbound;
    }

    private static JsonObject BuildRoute(RoutingSettings routing, ConnectionMode mode, string proxyTag = ProxyTag)
    {
        var rules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            new JsonObject { ["domain_suffix"] = ToArray(ConnectionCheckService.ProviderDomains), ["outbound"] = proxyTag },
        };
        if (routing.BypassPrivateNetworks)
        {
            rules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" });
        }

        if (routing.PreserveMeshVpnRanges)
        {
            rules.Add(new JsonObject { ["ip_cidr"] = ToArray(MeshVpnRanges), ["outbound"] = "direct" });
        }

        if (routing.BlockDomains.Count > 0)
        {
            rules.Add(new JsonObject { ["domain_suffix"] = ToArray(routing.BlockDomains), ["action"] = "reject" });
        }

        if (routing.DirectDomains.Count > 0)
        {
            rules.Add(new JsonObject { ["domain_suffix"] = ToArray(routing.DirectDomains), ["outbound"] = "direct" });
        }

        if (routing.DirectCidrs.Count > 0)
        {
            rules.Add(new JsonObject { ["ip_cidr"] = ToArray(routing.DirectCidrs), ["outbound"] = "direct" });
        }

        if (mode == ConnectionMode.Tun && routing.DirectProcesses.Count > 0)
        {
            rules.Add(new JsonObject { ["process_name"] = ToArray(routing.DirectProcesses), ["outbound"] = "direct" });
        }

        if (routing.ProxyOnlyListed && routing.ProxyDomains.Count > 0)
        {
            rules.Add(new JsonObject { ["domain_suffix"] = ToArray(routing.ProxyDomains), ["outbound"] = proxyTag });
        }

        return new JsonObject
        {
            ["rules"] = rules,
            ["final"] = routing.ProxyOnlyListed && routing.ProxyDomains.Count > 0 ? "direct" : proxyTag,
            ["auto_detect_interface"] = true,
            ["default_domain_resolver"] = "local",
        };
    }

    private static JsonObject BuildOutbound(Profile p, ProfileSecrets s, string tag = ProxyTag)
    {
        var o = new JsonObject { ["tag"] = tag, ["server"] = p.Host, ["server_port"] = p.Port };
        switch (p.Protocol)
        {
            case ProtocolKind.Vless:
                o["type"] = "vless";
                o["uuid"] = Require(s, Keys.Uuid);
                if (p.Option(Keys.Flow) is { } flow)
                {
                    o["flow"] = flow;
                }

                AddTls(o, p, defaultEnabled: false);
                AddTransport(o, p);
                break;
            case ProtocolKind.Vmess:
                o["type"] = "vmess";
                o["uuid"] = Require(s, Keys.Uuid);
                o["security"] = p.Option(Keys.Method) ?? "auto";
                o["alter_id"] = int.TryParse(p.Option("alterId"), out var aid) ? aid : 0;
                AddTls(o, p, defaultEnabled: false);
                AddTransport(o, p);
                break;
            case ProtocolKind.Trojan:
                o["type"] = "trojan";
                o["password"] = Require(s, Keys.Password);
                AddTls(o, p, defaultEnabled: true);
                AddTransport(o, p);
                break;
            case ProtocolKind.Shadowsocks:
                o["type"] = "shadowsocks";
                o["method"] = p.Option(Keys.Method) ?? "aes-256-gcm";
                o["password"] = Require(s, Keys.Password);
                if (p.Option(Keys.Plugin) is { } plugin)
                {
                    var semi = plugin.IndexOf(';');
                    o["plugin"] = semi > 0 ? plugin[..semi] : plugin;
                    if (semi > 0)
                    {
                        o["plugin_opts"] = plugin[(semi + 1)..];
                    }
                }

                break;
            case ProtocolKind.Hysteria2:
                o["type"] = "hysteria2";
                if (s[Keys.Password] is { } auth)
                {
                    o["password"] = auth;
                }

                if (p.Option("ports") is { } hop)
                {
                    o["server_ports"] = new JsonArray([.. hop.Split(',').Select(x => (JsonNode)JsonValue.Create(x.Trim().Replace('-', ':'))!)]);
                }

                if (int.TryParse(p.Option(Keys.UpMbps), out var up))
                {
                    o["up_mbps"] = up;
                }

                if (int.TryParse(p.Option(Keys.DownMbps), out var down))
                {
                    o["down_mbps"] = down;
                }

                if (p.Option(Keys.Obfs) is { } obfs)
                {
                    o["obfs"] = new JsonObject { ["type"] = obfs, ["password"] = s[Keys.ObfsPassword] ?? "" };
                }

                AddTls(o, p, defaultEnabled: true, forceAlpn: "h3");
                break;
            case ProtocolKind.Tuic:
                o["type"] = "tuic";
                o["uuid"] = Require(s, Keys.Uuid);
                o["password"] = Require(s, Keys.Password);
                o["congestion_control"] = p.Option(Keys.CongestionControl) ?? "bbr";
                if (p.Option(Keys.UdpRelayMode) is { } relay)
                {
                    o["udp_relay_mode"] = relay;
                }

                AddTls(o, p, defaultEnabled: true, forceAlpn: "h3");
                break;
            case ProtocolKind.Socks5:
                o["type"] = "socks";
                o["version"] = "5";
                AddUserPass(o, p, s);
                break;
            case ProtocolKind.Http:
                o["type"] = "http";
                AddUserPass(o, p, s);
                if (p.Option(Keys.Tls) == "true")
                {
                    o["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = p.Option(Keys.Sni) ?? p.Host };
                }

                break;
            default:
                throw new NotSupportedException(p.Protocol.ToString());
        }

        return o;
    }

    private static JsonObject BuildWireGuardEndpoint(Profile p, ProfileSecrets s, string tag = ProxyTag)
    {
        var raw = s[Keys.RawConfig] ?? throw new InvalidOperationException("WireGuard profile has no stored configuration.");
        var cfg = WireGuardConfig.Parse(raw);
        var ep = new JsonObject
        {
            ["type"] = "wireguard",
            ["tag"] = tag,
            ["address"] = ToArray(SplitList(cfg.Interface.GetValueOrDefault("Address", "10.0.0.2/32"))),
            ["private_key"] = cfg.Interface["PrivateKey"],
        };
        if (int.TryParse(cfg.Interface.GetValueOrDefault("MTU"), out var mtu))
        {
            ep["mtu"] = mtu;
        }

        // Optional WARP-style reserved bytes (issue #85), stored as a comma-separated option.
        // Endpoints share the tag namespace with outbounds, so an exit endpoint is a valid
        // route final / DNS detour target, like in a direct WireGuard connection.
        if (p.Option(Keys.Reserved) is { } reservedText)
        {
            ep["reserved"] = new JsonArray((TryParseReserved(reservedText)
                ?? throw new Engines.EngineException("The WireGuard 'reserved' option must be three bytes, e.g. '1,2,3'.", localizationKey: "error.engine.wireguard.reserved"))
                .Select(b => (JsonNode)JsonValue.Create((int)b)!).ToArray());
        }

        var peers = new JsonArray();
        foreach (var peer in cfg.Peers)
        {
            var (host, port) = WireGuardConfParser.SplitEndpoint(peer["Endpoint"]);
            var po = new JsonObject
            {
                ["address"] = host,
                ["port"] = port,
                ["public_key"] = peer.GetValueOrDefault("PublicKey", ""),
                ["allowed_ips"] = ToArray(SplitList(peer.GetValueOrDefault("AllowedIPs", "0.0.0.0/0, ::/0"))),
            };
            if (peer.TryGetValue("PresharedKey", out var psk))
            {
                po["pre_shared_key"] = psk;
            }

            if (int.TryParse(peer.GetValueOrDefault("PersistentKeepalive"), out var ka))
            {
                po["persistent_keepalive_interval"] = ka;
            }

            peers.Add(po);
        }

        ep["peers"] = peers;
        return ep;
    }

    private static void AddTls(JsonObject o, Profile p, bool defaultEnabled, string? forceAlpn = null)
    {
        var security = p.Option(Keys.Security) ?? (defaultEnabled ? "tls" : "none");
        if (security == "none" && !defaultEnabled)
        {
            return;
        }

        var tls = new JsonObject { ["enabled"] = true, ["server_name"] = p.Option(Keys.Sni) ?? p.Host };
        if (p.Option(Keys.AllowInsecure) == "true")
        {
            tls["insecure"] = true;
        }

        var alpn = p.Option(Keys.Alpn) ?? forceAlpn;
        if (alpn is not null)
        {
            tls["alpn"] = ToArray(SplitList(alpn));
        }

        if (p.Option(Keys.Fingerprint) is { } fp || security == "reality")
        {
            tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = p.Option(Keys.Fingerprint) ?? "chrome" };
        }

        if (security == "reality")
        {
            tls["reality"] = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = p.Option(Keys.RealityPublicKey) ?? "",
                ["short_id"] = p.Option(Keys.RealityShortId) ?? "",
            };
        }

        o["tls"] = tls;
    }

    private static void AddTransport(JsonObject o, Profile p)
    {
        var type = p.Option(Keys.Transport) ?? "tcp";
        JsonObject? t = type switch
        {
            "ws" => new JsonObject { ["type"] = "ws", ["path"] = p.Option(Keys.Path) ?? "/" },
            "grpc" => new JsonObject { ["type"] = "grpc", ["service_name"] = p.Option(Keys.ServiceName) ?? p.Option(Keys.Path) ?? "" },
            "http" or "h2" => new JsonObject { ["type"] = "http", ["path"] = p.Option(Keys.Path) ?? "/" },
            "httpupgrade" => new JsonObject { ["type"] = "httpupgrade", ["path"] = p.Option(Keys.Path) ?? "/" },
            "quic" => new JsonObject { ["type"] = "quic" },
            "tcp" or "raw" or "" => null,
            _ => throw new NotSupportedException($"Transport '{type}' is not supported by sing-box; use the Xray engine."),
        };
        if (t is null)
        {
            return;
        }

        if (p.Option(Keys.HostHeader) is { } host)
        {
            if (type == "ws")
            {
                t["headers"] = new JsonObject { ["Host"] = host };
            }
            else if (type is "http" or "h2")
            {
                t["host"] = ToArray(SplitList(host));
            }
            else if (type == "httpupgrade")
            {
                t["host"] = host;
            }
        }

        o["transport"] = t;
    }

    private static void AddUserPass(JsonObject o, Profile p, ProfileSecrets s)
    {
        if (p.Option(Keys.Username) is { } user)
        {
            o["username"] = user;
            o["password"] = s[Keys.Password] ?? "";
        }
    }

    private static string Require(ProfileSecrets s, string key) =>
        s[key] ?? throw new InvalidOperationException($"Profile is missing the '{key}' credential.");

    internal static IEnumerable<string> SplitList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static byte[]? TryParseReserved(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return null;
        }

        var bytes = new byte[3];
        for (var i = 0; i < 3; i++)
        {
            if (!byte.TryParse(parts[i], out bytes[i]))
            {
                return null;
            }
        }

        return bytes;
    }

    private static JsonArray ToArray(IEnumerable<string> items) => new([.. items.Select(i => (JsonNode)JsonValue.Create(i)!)]);
}
