using System.Text;
using System.Text.Json;
using Bydlo.Core.Models;
using Bydlo.Core.Import.Parsers;

namespace Bydlo.Core.Import;

/// <summary>Exports credentials only on explicit user request; never persists or logs the result.</summary>
public static class ProfileExporter
{
    public static bool HasShareLink(ProtocolKind protocol) => protocol is
        ProtocolKind.Vless or ProtocolKind.Vmess or ProtocolKind.Trojan or ProtocolKind.Shadowsocks or
        ProtocolKind.Hysteria2 or ProtocolKind.Tuic or ProtocolKind.Socks5 or ProtocolKind.Http or
        ProtocolKind.AnyTls or ProtocolKind.ShadowTls or ProtocolKind.Naive or ProtocolKind.Ssh;

    public static bool HasConfigFile(ProtocolKind protocol) => protocol is
        ProtocolKind.WireGuard or ProtocolKind.AmneziaWG or ProtocolKind.OpenVpn;

    public static string ExportConfig(Profile profile, ProfileSecrets secrets)
    {
        if (!HasConfigFile(profile.Protocol)) { throw new NotSupportedException("No configuration file format."); }
        return Required(secrets, Keys.RawConfig);
    }

    public static string ExportLink(Profile p, ProfileSecrets secrets)
    {
        if (!HasShareLink(p.Protocol)) { throw new NotSupportedException("No share link format."); }
        if (string.IsNullOrWhiteSpace(p.Host) || p.Port is <= 0 or > 65535)
        {
            throw new InvalidOperationException("Invalid endpoint.");
        }

        if (p.Protocol == ProtocolKind.Vmess) { return Vmess(p, secrets); }

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        void Copy(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (p.Option(key) is { } value) { query[key] = value; }
            }
        }

        string scheme;
        string auth;
        switch (p.Protocol)
        {
            case ProtocolKind.AnyTls:
            case ProtocolKind.ShadowTls:
                scheme = p.Protocol == ProtocolKind.AnyTls ? "anytls" : "shadowtls";
                auth = Escape(p.Protocol == ProtocolKind.AnyTls ? Required(secrets, Keys.Password) : secrets[SingBoxUriParser.ShadowPassword] ?? "");
                Copy(Keys.Sni, Keys.Alpn, Keys.Fingerprint);
                if (p.Option(Keys.AllowInsecure) == "true") { query["insecure"] = "1"; }
                if (p.Protocol == ProtocolKind.ShadowTls)
                {
                    Copy("version", Keys.Method);
                    query["ss_password"] = Required(secrets, Keys.Password);
                }
                break;
            case ProtocolKind.Naive:
                scheme = p.Option("quic") == "true" ? "naive+quic" : "naive+https";
                auth = Escape(Required(secrets, Keys.Username)) + ":" + Escape(Required(secrets, Keys.Password));
                Copy(Keys.Sni);
                break;
            case ProtocolKind.Ssh:
                scheme = "ssh";
                auth = Escape(Required(secrets, Keys.Username));
                if (secrets[Keys.Password] is { } sshPassword) { auth += ":" + Escape(sshPassword); }
                foreach (var key in new[] { SingBoxUriParser.PrivateKey, SingBoxUriParser.Passphrase })
                {
                    if (secrets[key] is { } value) { query[key] = value; }
                }
                Copy("host_key");
                break;
            case ProtocolKind.Vless:
            case ProtocolKind.Trojan:
                scheme = p.Protocol == ProtocolKind.Vless ? "vless" : "trojan";
                auth = Escape(Required(secrets, p.Protocol == ProtocolKind.Vless ? Keys.Uuid : Keys.Password));
                Copy(Keys.Security, Keys.Transport, Keys.Sni, Keys.Fingerprint, Keys.Alpn, Keys.RealityPublicKey,
                    Keys.PinnedPeerCertSha256, Keys.VerifyPeerCertByName,
                    Keys.RealityShortId, Keys.RealitySpiderX, Keys.Path, Keys.HostHeader, Keys.ServiceName,
                    Keys.Flow, Keys.XhttpMode, "headerType");
                if (p.Protocol == ProtocolKind.Vless) { query["encryption"] = "none"; }
                if (p.Option(Keys.AllowInsecure) == "true") { query["allowInsecure"] = "1"; }
                break;
            case ProtocolKind.Shadowsocks:
                scheme = "ss";
                auth = Base64((p.Option(Keys.Method) ?? throw new InvalidOperationException("Missing cipher.")) + ":" + Required(secrets, Keys.Password))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                Copy(Keys.Plugin);
                break;
            case ProtocolKind.Hysteria2:
                scheme = "hysteria2";
                auth = Escape(secrets[Keys.Password] ?? "");
                Copy(Keys.Sni, Keys.Obfs, Keys.Alpn, Keys.UpMbps, Keys.DownMbps, "pinSHA256");
                if (secrets[Keys.ObfsPassword] is { } obfs) { query[Keys.ObfsPassword] = obfs; }
                if (p.Option(Keys.AllowInsecure) == "true") { query["insecure"] = "1"; }
                break;
            case ProtocolKind.Tuic:
                scheme = "tuic";
                auth = Escape(Required(secrets, Keys.Uuid)) + ":" + Escape(Required(secrets, Keys.Password));
                Copy(Keys.Sni, Keys.Alpn, Keys.CongestionControl, Keys.UdpRelayMode);
                if (p.Option(Keys.AllowInsecure) == "true") { query["allow_insecure"] = "1"; }
                break;
            default:
                scheme = p.Protocol == ProtocolKind.Socks5 ? "socks5" : p.Option(Keys.Tls) == "true" ? "https" : "http";
                auth = p.Option(Keys.Username) is { } user
                    ? Escape(user) + (secrets.Values.TryGetValue(Keys.Password, out var password) ? ":" + Escape(password) : "") : "";
                break;
        }

        var host = p.Host.Contains(':') ? "[" + p.Host.Trim('[', ']') + "]" : p.Host;
        var ports = p.Protocol == ProtocolKind.Hysteria2 && p.Option("ports") is { } hop ? "," + hop : "";
        var suffix = query.Count == 0 ? "" : "?" + string.Join('&', query.Select(kv => Escape(kv.Key) + "=" + Escape(kv.Value)));
        // Hysteria's empty authentication is legal; keep @ so port hopping remains unambiguous.
        var userInfo = auth.Length > 0 || p.Protocol == ProtocolKind.Hysteria2 ? auth + "@" : "";
        return $"{scheme}://{userInfo}{host}:{p.Port}{ports}/{suffix}#{Escape(p.Name)}";
    }

    private static string Vmess(Profile p, ProfileSecrets secrets)
    {
        var fields = new Dictionary<string, string>
        {
            ["v"] = "2", ["ps"] = p.Name, ["add"] = p.Host, ["port"] = p.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["id"] = Required(secrets, Keys.Uuid), ["net"] = p.Option(Keys.Transport) ?? "tcp",
            ["tls"] = p.Option(Keys.Security) ?? "none", ["scy"] = p.Option(Keys.Method) ?? "auto",
        };
        foreach (var (field, key) in new[] { ("aid", "alterId"), ("type", "headerType"), ("host", Keys.HostHeader),
            ("sni", Keys.Sni), ("alpn", Keys.Alpn), ("fp", Keys.Fingerprint),
            ("pcs", Keys.PinnedPeerCertSha256), ("vcn", Keys.VerifyPeerCertByName),
            ("path", p.Option(Keys.Transport) == "grpc" ? Keys.ServiceName : Keys.Path) })
        {
            if (p.Option(key) is { } value) { fields[field] = value; }
        }
        return "vmess://" + Base64(JsonSerializer.Serialize(fields));
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string Required(ProfileSecrets secrets, string key) => secrets[key] ?? throw new InvalidOperationException("Required credentials are unavailable.");
}
