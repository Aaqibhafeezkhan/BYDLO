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
        ProtocolKind.Vless, ProtocolKind.Vmess, ProtocolKind.Trojan, ProtocolKind.Shadowsocks, ProtocolKind.Socks5, ProtocolKind.Http,
    };

    public string BuildJson(Profile profile, ProfileSecrets secrets, BuildContext ctx) =>
        Build(profile, secrets, ctx).ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public JsonObject Build(Profile p, ProfileSecrets s, BuildContext ctx)
    {
        if (!Supported.Contains(p.Protocol))
        {
            throw new NotSupportedException($"Xray adapter does not handle {p.Protocol}.");
        }

        var settings = ctx.Settings;
        var routing = settings.Routing;
        var rules = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "field",
                ["domain"] = Arr(ConnectionCheckService.ProviderDomains.Select(d => "domain:" + d)),
                ["outboundTag"] = "proxy",
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

        if (routing.DirectProcesses.Count > 0)
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
                BuildOutbound(p, s),
                new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" },
                new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" }),
            ["routing"] = new JsonObject { ["domainStrategy"] = "IPIfNonMatch", ["rules"] = rules },
        };
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

    private static JsonObject BuildOutbound(Profile p, ProfileSecrets s)
    {
        var o = new JsonObject { ["tag"] = "proxy" };
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
                return o;
            case ProtocolKind.Socks5:
            case ProtocolKind.Http:
                o["protocol"] = p.Protocol == ProtocolKind.Socks5 ? "socks" : "http";
                var server = new JsonObject { ["address"] = p.Host, ["port"] = p.Port };
                if (p.Option(Keys.Username) is { } user)
                {
                    server["users"] = new JsonArray(new JsonObject { ["user"] = user, ["pass"] = s[Keys.Password] ?? "" });
                }

                o["settings"] = new JsonObject { ["servers"] = new JsonArray(server) };
                return o;
        }

        o["streamSettings"] = BuildStream(p, p.Protocol == ProtocolKind.Trojan ? "tls" : "none");
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
