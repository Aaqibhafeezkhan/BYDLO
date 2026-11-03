using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;

namespace Bydlo.Core.Security;

/// <summary>The privileged boundary accepts profile data, never client-owned files or program paths.</summary>
public static class HelperRequestPolicy
{
    public static bool Supports(EngineKind kind) => kind is EngineKind.SingBox or EngineKind.Zapret or EngineKind.AmneziaWG or EngineKind.OpenVpn;

    public static void Validate(HelperRequest request)
    {
        if (request.Version != HelperProtocol.Version || !Enum.IsDefined(request.Operation) || !Supports(request.Engine))
        { throw new InvalidDataException("Unsupported helper request."); }
        ValidateSettings(request.Settings);
        if (request.ConnectionId is { Length: > 256 } || request.Operation == HelperOperation.CloseConnection && string.IsNullOrEmpty(request.ConnectionId))
        { throw new InvalidDataException("Invalid connection id."); }
        if (request.Operation is not (HelperOperation.Start or HelperOperation.Reconnect)) { return; }
        var start = request.Start ?? throw new InvalidDataException("Missing start request.");
        ValidateSettings(start.Settings);
        if (start.RuleSetDirectory is not null || start.Chain is not null)
        { throw new InvalidDataException("External rule sets and relay chains are unavailable in helper mode."); }
        if (request.Engine != EngineKind.Zapret)
        {
            var profile = start.Profile ?? throw new InvalidDataException("Missing profile.");
            if (profile.Protocol is ProtocolKind.SingBoxConfig or ProtocolKind.XrayConfig || profile.Option(Keys.Plugin) is not null)
            { throw new InvalidDataException("Custom configs and executable plugins are unavailable in helper mode."); }
            if (request.Engine == EngineKind.OpenVpn)
            { ValidateOpenVpn(start.Secrets[Keys.RawConfig] ?? ""); }
        }
    }

    private static void ValidateSettings(AppSettings settings)
    {
        if (settings.EnginePaths.Count != 0 || !string.IsNullOrEmpty(settings.Zapret.InstallPath) || settings.MockMode)
        { throw new InvalidDataException("Only bundled engines are allowed by the helper."); }
        if (settings.Routing.Preset != RoutingPreset.All || settings.Routing.RemoteDnsRuleSets.Count != 0 ||
            settings.Routing.DirectDnsRuleSets.Count != 0 || settings.Routing.OrderedRules.Any(r =>
                r.Condition is Routing.RoutingCondition.GeoSite or Routing.RoutingCondition.GeoIp))
        { throw new InvalidDataException("External rule sets are unavailable in helper mode."); }
        if (settings.Zapret.Strategy is { } strategy &&
            (strategy.Contains('/') || strategy.Contains('\\') || strategy.Contains(':') || strategy.Contains("..", StringComparison.Ordinal)))
        { throw new InvalidDataException("Invalid strategy name."); }
    }

    // Explicit allow-list: OpenVPN has many file-writing, code-loading and command-execution directives.
    // An imported .ovpn is not a trusted program. Certificates and keys must be inline.
    public static void ValidateOpenVpn(string config)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "client", "tls-client", "dev", "dev-type", "proto", "remote", "remote-random", "resolv-retry",
            "nobind", "persist-key", "persist-tun", "remote-cert-tls", "verify-x509-name", "cipher",
            "data-ciphers", "data-ciphers-fallback", "auth", "auth-nocache", "auth-user-pass", "verb",
            "mute", "mute-replay-warnings", "keepalive", "ping", "ping-restart", "connect-retry",
            "connect-timeout", "explicit-exit-notify", "redirect-gateway", "route", "route-ipv6",
            "route-metric", "route-delay", "dhcp-option", "pull", "pull-filter", "tun-mtu", "mssfix",
            "sndbuf", "rcvbuf", "key-direction", "tls-version-min", "tls-version-max", "tls-cipher",
            "tls-ciphersuites", "reneg-sec", "float", "remote-random-hostname", "auth-retry",
        };
        var inline = new HashSet<string>(StringComparer.Ordinal) { "ca", "cert", "key", "tls-auth", "tls-crypt", "tls-crypt-v2" };
        string? block = null;
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.Trim();
            if (block is not null)
            {
                if (line == "</" + block + ">") { block = null; }
                else if (line.Contains('<') || line.Contains('>') || line.Contains('\0'))
                { throw new InvalidDataException("Invalid inline block."); }
                continue;
            }
            if (line.Length == 0 || line[0] is '#' or ';') { continue; }
            if (line[0] == '<' && line[^1] == '>' && inline.Contains(line[1..^1]))
            { block = line[1..^1]; continue; }
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!allowed.Contains(parts[0]) || line.Contains('\0') || line.Contains('\\') ||
                (parts[0] == "auth-user-pass" && parts.Length != 1) ||
                (parts[0] == "dev" && (parts.Length != 2 || parts[1] != "tun")))
            { throw new InvalidDataException("Unsafe or unsupported OpenVPN directive."); }
        }
        if (block is not null || config.Length == 0) { throw new InvalidDataException("Invalid inline OpenVPN configuration."); }
    }
}
