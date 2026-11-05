using System.Text.Json.Serialization;

namespace Bydlo.Core.Models;

/// <summary>
/// A connection profile. Holds only non-secret options; credentials live in <see cref="Security.ISecretStore"/>
/// under <see cref="Id"/>.
/// </summary>
public sealed class Profile
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public ProtocolKind Protocol { get; init; }

    public string Host { get; set; } = "";

    public int Port { get; set; }

    /// <summary>Non-secret protocol options (sni, transport, path, fingerprint, REALITY public key...).</summary>
    public Dictionary<string, string> Options { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Subscription the profile came from, if any.</summary>
    public Guid? SubscriptionId { get; set; }

    /// <summary>Forces a specific engine; <c>null</c> lets <see cref="Engines.EngineRegistry"/> pick.</summary>
    public EngineKind? PreferredEngine { get; set; }

    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool IsFavorite { get; set; }

    [JsonIgnore]
    public string Endpoint => string.IsNullOrEmpty(Host) ? "" : Port > 0 ? $"{Host}:{Port}" : Host;

    public string? Option(string key) => Options.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}

/// <summary>Secret values that belong to a profile (uuid, password, private keys, raw configs).</summary>
public sealed class ProfileSecrets
{
    public Dictionary<string, string> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? this[string key] => Values.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    public static ProfileSecrets Empty => new();
}

/// <summary>Result of parsing an import source.</summary>
public sealed record ImportedProfile(Profile Profile, ProfileSecrets Secrets);

/// <summary>Well-known option and secret keys used across parsers and builders.</summary>
public static class Keys
{
    // Options (non-secret)
    public const string Security = "security";       // none | tls | reality
    public const string Sni = "sni";
    public const string Fingerprint = "fp";
    public const string Alpn = "alpn";
    public const string AllowInsecure = "insecure";
    public const string PinnedPeerCertSha256 = "pcs";
    public const string VerifyPeerCertByName = "vcn";
    public const string Transport = "type";          // tcp | ws | grpc | http | h2 | httpupgrade | xhttp | quic
    public const string Path = "path";
    public const string HostHeader = "host";
    public const string ServiceName = "serviceName";
    public const string Flow = "flow";
    public const string RealityPublicKey = "pbk";
    public const string RealityShortId = "sid";
    public const string RealitySpiderX = "spx";
    public const string Method = "method";           // shadowsocks cipher / vmess security
    public const string Plugin = "plugin";
    public const string Obfs = "obfs";
    public const string CongestionControl = "congestion_control";
    public const string UdpRelayMode = "udp_relay_mode";
    public const string UpMbps = "upmbps";
    public const string DownMbps = "downmbps";
    public const string XhttpMode = "mode";
    public const string Encryption = "encryption";
    public const string XhttpExtra = "extra";
    public const string HeaderType = "headerType";
    public const string Seed = "seed";
    public const string Username = "username";
    public const string Tls = "tls";

    /// <summary>WireGuard <c>reserved</c> bytes (comma-separated, e.g. WARP client id).</summary>
    public const string Reserved = "reserved";

    // Secrets
    public const string Uuid = "uuid";
    public const string Password = "password";
    public const string ObfsPassword = "obfs-password";
    public const string RawConfig = "config";

    /// <summary>XHTTP transport headers (e.g. Authorization bearer, Cookie) as a JSON object. Never in profile Options (issue #338).</summary>
    public const string XhttpHeaders = "xhttp-headers";

    /// <summary>Cloudflare WARP account id from in-app registration (issue #85).</summary>
    public const string WarpAccountId = "warp-account-id";

    /// <summary>Cloudflare WARP API token from in-app registration. Never logged.</summary>
    public const string WarpToken = "warp-token";

    /// <summary>Cloudflare WARP license key returned at registration.</summary>
    public const string WarpLicense = "warp-license";
}
