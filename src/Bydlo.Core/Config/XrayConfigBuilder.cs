using Bydlo.Core.Import.Parsers;
using Bydlo.Core.Routing;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;

namespace Bydlo.Core.Config;

/// <summary>
/// Generates Xray-core configuration. Used for transports sing-box lacks (XHTTP) and as an alternative engine
/// for VLESS / VMess / Trojan / Shadowsocks / SOCKS / HTTP. Local proxy mode only.
/// </summary>
public sealed class XrayConfigBuilder
{
    public static readonly IReadOnlySet<ProtocolKind> Supported = new HashSet<ProtocolKind>
    {
        ProtocolKind.Vless, ProtocolKind.Vmess, ProtocolKind.Trojan, ProtocolKind.Shadowsocks, ProtocolKind.Socks5, ProtocolKind.Http, ProtocolKind.XrayConfig,
    };

    public string BuildJson(Profile profile, ProfileSecrets secrets, BuildContext ctx) =>
        Build(profile, secrets, ctx).ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// Builds a relay chain: traffic enters at <c>hops[0]</c> and leaves at the last hop.
    /// Every hop routes through the previous one via <c>proxySettings</c>.
    /// The exit hop is listed first so Xray's default outbound (used when no rule matches)
    /// carries the whole chain; routing rules point at the exit hop explicitly.
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
                throw new Engines.EngineException($"The {hop.Profile.Protocol} hop cannot be chained by Xray.", localizationKey: "error.chain.engine");
            }
        }

        var auxiliary = new List<JsonObject>();
        var proxies = hops.Select((h, i) => BuildOutbound(h.Profile, h.Secrets, ctx.Settings.DpiDefaults, auxiliary,
            Tag(i), i > 0 ? Tag(i - 1) : null)).Reverse().ToList();
        proxies.AddRange(auxiliary);
        return BuildCore(ctx, proxies, Tag(hops.Count - 1));
    }

    internal static string Tag(int index) => $"proxy-{index}";

    public JsonObject Build(Profile p, ProfileSecrets s, BuildContext ctx)
    {
        if (p.Protocol == ProtocolKind.XrayConfig)
        {
            return BuildCustom(s, ctx);
        }

        if (!Supported.Contains(p.Protocol))
        {
            throw new NotSupportedException($"Xray adapter does not handle {p.Protocol}.");
        }

        var auxiliary = new List<JsonObject>();
        var proxy = BuildOutbound(p, s, ctx.Settings.DpiDefaults, auxiliary);
        return BuildCore(ctx, [proxy, .. auxiliary], "proxy");
    }

    private static JsonObject BuildCustom(ProfileSecrets secrets, BuildContext ctx)
    {
        JsonObject custom;
        try
        {
            custom = CustomJsonConfig.Read(secrets[Keys.RawConfig] ?? "", ProtocolKind.XrayConfig);
            CustomInboundRouting.Map(custom, false, ["socks-in", "http-in"]);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidOperationException)
        {
            throw new EngineException("import.json.unsafe", localizationKey: "import.json.unsafe");
        }

        var template = BuildCore(ctx, [], "proxy");
        custom["inbounds"] = template["inbounds"]!.DeepClone();
        custom["log"] = template["log"]!.DeepClone();
        var outbounds = custom["outbounds"]!.AsArray();
        var direct = outbounds.OfType<JsonObject>().FirstOrDefault(node => (string?)node["protocol"] == "freedom");
        var directTag = (string?)direct?["tag"] ?? "bydlo-direct";
        if (direct is null)
        {
            if (outbounds.OfType<JsonObject>().Any(node => (string?)node["tag"] == directTag))
            {
                throw new EngineException("import.json.unsafe", localizationKey: "import.json.unsafe");
            }
            outbounds.Add(new JsonObject { ["protocol"] = "freedom", ["tag"] = directTag });
        }
        var routing = custom["routing"] as JsonObject ?? new JsonObject();
        var rules = routing["rules"] as JsonArray ?? new JsonArray();
        if (ctx.Settings.Routing.PreserveMeshVpnRanges)
        {
            rules.Insert(0, new JsonObject { ["type"] = "field", ["ip"] = Arr(SingBoxConfigBuilder.MeshVpnRanges), ["outboundTag"] = directTag });
        }
        if (ctx.Settings.Routing.BypassPrivateNetworks)
        {
            rules.Insert(0, new JsonObject { ["type"] = "field", ["ip"] = new JsonArray("geoip:private"), ["outboundTag"] = directTag });
        }
        if (rules.Parent is null) { routing["rules"] = rules; }
        if (routing.Parent is null) { custom["routing"] = routing; }
        return custom;
    }

    private static JsonObject BuildCore(BuildContext ctx, List<JsonObject> proxies, string proxyTag)
    {
        var settings = ctx.Settings;
        var routing = settings.Routing;
        var rules = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "field",
                ["domain"] = Arr(ConnectionCheckService.ProviderDomains.Select(d => "domain:" + d)),
                ["outboundTag"] = proxyTag,
            },
        };
        if (routing.BypassPrivateNetworks)
        {
            rules.Add(new JsonObject { ["type"] = "field", ["ip"] = new JsonArray("geoip:private"), ["outboundTag"] = "direct" });
        }

        if (routing.PreserveMeshVpnRanges)
        {
            rules.Add(new JsonObject { ["type"] = "field", ["ip"] = Arr(SingBoxConfigBuilder.MeshVpnRanges), ["outboundTag"] = "direct" });
        }

        foreach (var rule in routing.OrderedRules)
        {
            if (RoutingPresets.UnsupportedXrayRule(routing, rule))
            {
                throw new EngineException("routing.rules.unsupported", localizationKey: "routing.rules.unsupported");
            }
            rules.Add(OrderedRoutingRules.Compile(rule, false, settings.Mode, proxyTag, null));
        }

        if (routing.BlockDomains.Count > 0)
        {
            rules.Add(new JsonObject { ["type"] = "field", ["domain"] = Arr(routing.BlockDomains.Select(d => "domain:" + d)), ["outboundTag"] = "block" });
        }

        if (routing.DirectDomains.Count > 0)
        {
            rules.Add(new JsonObject { ["type"] = "field", ["domain"] = Arr(routing.DirectDomains.Select(d => "domain:" + d)), ["outboundTag"] = "direct" });
        }

        if (routing.DirectCidrs.Count > 0)
        {
            rules.Add(new JsonObject { ["type"] = "field", ["ip"] = Arr(routing.DirectCidrs), ["outboundTag"] = "direct" });
        }

        if (!routing.ProxyOnlyListedProcesses && routing.DirectProcesses.Count > 0)
        {
            rules.Add(new JsonObject { ["type"] = "field", ["process"] = Arr(routing.DirectProcesses), ["outboundTag"] = "direct" });
        }

        if (routing.ProxyDomains.Count > 0)
        {
            rules.Add(new JsonObject { ["type"] = "field", ["domain"] = Arr(routing.ProxyDomains.Select(d => "domain:" + d)), ["outboundTag"] = "proxy" });
        }

        if (routing.ProxyOnlyListed && routing.ProxyDomains.Count > 0)
        {
            rules.Add(new JsonObject { ["type"] = "field", ["network"] = "tcp,udp", ["outboundTag"] = "direct" });
        }

        var lanAuth = ctx.LocalProxyAuth;
        var listen = lanAuth is null ? "127.0.0.1" : "0.0.0.0";
        var socksSettings = new JsonObject { ["udp"] = true };
        var httpSettings = new JsonObject();
        if (lanAuth is not null)
        {
            // Without "password" Xray falls back to NO_AUTH and ignores accounts.
            socksSettings["auth"] = "password";
            var accounts = new JsonArray(new JsonObject { ["user"] = lanAuth.Username, ["pass"] = lanAuth.Password });
            socksSettings["accounts"] = accounts;
            httpSettings["accounts"] = accounts.DeepClone();
        }

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["inbounds"] = new JsonArray(
                new JsonObject
                {
                    ["tag"] = "socks-in",
                    ["listen"] = listen,
                    ["port"] = settings.LocalProxyPort,
                    ["protocol"] = "socks",
                    ["settings"] = socksSettings,
                    ["sniffing"] = new JsonObject { ["enabled"] = true, ["destOverride"] = new JsonArray("http", "tls", "quic") },
                },
                new JsonObject
                {
                    ["tag"] = "http-in",
                    ["listen"] = listen,
                    ["port"] = settings.LocalProxyPort + 1,
                    ["protocol"] = "http",
                    ["settings"] = httpSettings,
                }),
            ["outbounds"] = new JsonArray(
                [.. proxies,
                new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" },
                new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" }]),
            ["routing"] = new JsonObject { ["domainStrategy"] = "IPIfNonMatch", ["rules"] = rules },
        };
        if (routing.RemoteDnsTransport != DnsTransport.Https || routing.RemoteDns != "1.1.1.1"
            || routing.DirectDnsTransport != DnsTransport.Udp || routing.DirectDns != "local"
            || !routing.RemoteDnsThroughProxy || routing.DirectDnsThroughProxy
            || routing.RemoteDnsDomains.Count + routing.DirectDnsDomains.Count +
                routing.RemoteDnsRuleSets.Count + routing.DirectDnsRuleSets.Count > 0)
        {
            root["dns"] = BuildDns(routing, rules, proxyTag);
        }

        if (ctx.ClashApiPort is { } statsPort)
        {
            root["stats"] = new JsonObject();
            root["api"] = new JsonObject { ["tag"] = "api", ["listen"] = $"127.0.0.1:{statsPort}", ["services"] = new JsonArray("StatsService") };
            root["policy"] = new JsonObject
            {
                ["system"] = new JsonObject { ["statsOutboundUplink"] = true, ["statsOutboundDownlink"] = true },
            };
        }

        return root;
    }

    private static JsonObject BuildDns(RoutingSettings routing, JsonArray routeRules, string proxyTag)
    {
        if (routing.RemoteDnsRuleSets.Count + routing.DirectDnsRuleSets.Count > 0)
        {
            throw new EngineException("routing.dns.xray", localizationKey: "routing.dns.xray");
        }

        var localDomains = routing.DirectDomains.Concat(routing.DirectDnsDomains).Distinct().ToArray();
        var servers = new JsonArray();
        if (localDomains.Length > 0)
        {
            servers.Add(Server("dns-local", routing.DirectDns, routing.DirectDnsTransport, routing.DirectDnsThroughProxy, localDomains));
        }
        servers.Add(Server("dns-remote", routing.RemoteDns, routing.RemoteDnsTransport, routing.RemoteDnsThroughProxy,
            routing.RemoteDnsDomains));
        return new JsonObject { ["servers"] = servers, ["disableFallbackIfMatch"] = true };

        JsonObject Server(string tag, string host, DnsTransport transport, bool throughProxy, IEnumerable<string> domains)
        {
            var target = host;
            if (transport == DnsTransport.System || host is "local" or "")
            {
                target = "localhost";
            }
            else
            {
                var address = System.Net.IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? "[" + host + "]" : host;
                target = transport switch
                {
                    DnsTransport.Udp when System.Net.IPAddress.TryParse(host, out _) => host,
                    DnsTransport.Tcp => "tcp://" + address,
                    DnsTransport.Https => "https://" + address + "/dns-query",
                    DnsTransport.Quic when !throughProxy => "quic+local://" + address,
                    _ => throw new EngineException("routing.dns.xray", localizationKey: "routing.dns.xray"),
                };
                routeRules.Insert(0, new JsonObject
                {
                    ["type"] = "field", ["inboundTag"] = new JsonArray(tag), ["outboundTag"] = throughProxy ? proxyTag : "direct",
                });
            }
            var server = new JsonObject { ["address"] = target, ["tag"] = tag };
            var matches = Arr(domains.Select(domain => "domain:" + domain));
            if (matches.Count > 0)
            {
                server["domains"] = matches;
                server["skipFallback"] = tag == "dns-local";
            }
            return server;
        }
    }

    private static JsonObject BuildOutbound(Profile p, ProfileSecrets s, DpiSettings defaults, List<JsonObject> auxiliary,
        string tag = "proxy", string? via = null)
    {
        var o = new JsonObject { ["tag"] = tag };
        var needsStream = true;
        switch (p.Protocol)
        {
            case ProtocolKind.Vless:
                o["protocol"] = "vless";
                var vlessUser = new JsonObject { ["id"] = s[Keys.Uuid] ?? "", ["encryption"] = "none" };
                if (p.Option(Keys.Flow) is { } flow)
                {
                    vlessUser["flow"] = flow;
                }

                o["settings"] = Vnext(p, vlessUser);
                break;
            case ProtocolKind.Vmess:
                o["protocol"] = "vmess";
                o["settings"] = Vnext(p, new JsonObject { ["id"] = s[Keys.Uuid] ?? "", ["security"] = p.Option(Keys.Method) ?? "auto" });
                break;
            case ProtocolKind.Trojan:
                o["protocol"] = "trojan";
                o["settings"] = new JsonObject
                {
                    ["servers"] = new JsonArray(new JsonObject { ["address"] = p.Host, ["port"] = p.Port, ["password"] = s[Keys.Password] ?? "" }),
                };
                break;
            case ProtocolKind.Shadowsocks:
                o["protocol"] = "shadowsocks";
                o["settings"] = new JsonObject
                {
                    ["servers"] = new JsonArray(new JsonObject
                    {
                        ["address"] = p.Host, ["port"] = p.Port, ["method"] = p.Option(Keys.Method) ?? "aes-256-gcm", ["password"] = s[Keys.Password] ?? "",
                    }),
                };
                needsStream = false;
                break;
            case ProtocolKind.Socks5:
            case ProtocolKind.Http:
                o["protocol"] = p.Protocol == ProtocolKind.Socks5 ? "socks" : "http";
                var server = new JsonObject { ["address"] = p.Host, ["port"] = p.Port };
                if (p.Option(Keys.Username) is { } user)
                {
                    server["users"] = new JsonArray(new JsonObject { ["user"] = user, ["pass"] = s[Keys.Password] ?? "" });
                }

                o["settings"] = new JsonObject { ["servers"] = new JsonArray(server) };
                needsStream = false;
                break;
        }

        if (needsStream)
        {
            o["streamSettings"] = BuildStream(p, p.Protocol == ProtocolKind.Trojan ? "tls" : "none");
        }

        // Chain relay: dial the server through the previous hop.
        if (via is not null)
        {
            o["proxySettings"] = new JsonObject { ["tag"] = via, ["transportLayer"] = true };
        }

        var dpi = defaults.For(p);
        dpi.Validate(p, EngineKind.Xray);
        if (dpi.Fragment != "off")
        {
            if (via is not null || o["streamSettings"] is not JsonObject stream ||
                (string?)stream["security"] is not ("tls" or "reality"))
            {
                throw new EngineException("profile.dpi.unsupported", localizationKey: "profile.dpi.unsupported");
            }

            var dialer = tag + "-fragment";
            stream["sockopt"] = new JsonObject { ["dialerProxy"] = dialer };
            auxiliary.Add(new JsonObject
            {
                ["tag"] = dialer,
                ["protocol"] = "freedom",
                ["settings"] = new JsonObject
                {
                    ["fragment"] = new JsonObject { ["packets"] = "tlshello", ["length"] = dpi.Size, ["interval"] = dpi.Interval },
                },
            });
        }
        if (dpi.Padding && o["streamSettings"]?["xhttpSettings"] is JsonObject xhttp)
        {
            xhttp["xPaddingBytes"] = dpi.Size;
        }
        if (dpi.Mux != "off")
        {
            o["mux"] = new JsonObject
            {
                ["enabled"] = true,
                ["concurrency"] = dpi.Mux == "xudp" ? -1 : 8,
                ["xudpConcurrency"] = dpi.Mux == "xudp" ? 16 : -1,
            };
        }
        return o;
    }

    private static JsonObject Vnext(Profile p, JsonObject user) => new()
    {
        ["vnext"] = new JsonArray(new JsonObject { ["address"] = p.Host, ["port"] = p.Port, ["users"] = new JsonArray(user) }),
    };

    private static JsonObject BuildStream(Profile p, string defaultSecurity)
    {
        var network = p.Option(Keys.Transport) ?? "tcp";
        if (network is "h2")
        {
            network = "http";
        }

        if (network == "splithttp")
        {
            network = "xhttp";
        }

        var security = p.Option(Keys.Security) ?? defaultSecurity;
        var stream = new JsonObject { ["network"] = network == "tcp" ? "raw" : network, ["security"] = security };
        var sni = p.Option(Keys.Sni) ?? p.Host;
        if (security == "reality")
        {
            stream["realitySettings"] = new JsonObject
            {
                ["serverName"] = sni,
                ["fingerprint"] = p.Option(Keys.Fingerprint) ?? "chrome",
                ["publicKey"] = p.Option(Keys.RealityPublicKey) ?? "",
                ["shortId"] = p.Option(Keys.RealityShortId) ?? "",
                ["spiderX"] = p.Option(Keys.RealitySpiderX) ?? "",
            };
        }
        else if (security == "tls")
        {
            var tls = new JsonObject { ["serverName"] = sni, ["fingerprint"] = p.Option(Keys.Fingerprint) ?? "chrome" };
            if (p.Option(Keys.Alpn) is { } alpn)
            {
                tls["alpn"] = Arr(SingBoxConfigBuilder.SplitList(alpn));
            }

            if (p.Option(Keys.AllowInsecure) == "true")
            {
                tls["allowInsecure"] = true;
            }

            stream["tlsSettings"] = tls;
        }

        var host = p.Option(Keys.HostHeader);
        switch (network)
        {
            case "ws":
                stream["wsSettings"] = new JsonObject { ["path"] = p.Option(Keys.Path) ?? "/", ["host"] = host ?? "" };
                break;
            case "grpc":
                stream["grpcSettings"] = new JsonObject { ["serviceName"] = p.Option(Keys.ServiceName) ?? "" };
                break;
            case "httpupgrade":
                stream["httpupgradeSettings"] = new JsonObject { ["path"] = p.Option(Keys.Path) ?? "/", ["host"] = host ?? "" };
                break;
            case "xhttp":
                stream["xhttpSettings"] = new JsonObject { ["path"] = p.Option(Keys.Path) ?? "/", ["host"] = host ?? "", ["mode"] = p.Option(Keys.XhttpMode) ?? "auto" };
                break;
            case "http":
                throw new NotSupportedException("The HTTP/2 transport was removed from Xray-core; use the sing-box engine for this profile.");
        }

        return stream;
    }

    private static JsonArray Arr(IEnumerable<string> items) => new([.. items.Select(i => (JsonNode)JsonValue.Create(i)!)]);
}
