namespace Bydlo.Core.Security;

/// <summary>Shared policy for imported OpenVPN profiles and every engine launch path.</summary>
public static class OpenVpnConfigPolicy
{
    // Explicit allow-list: OpenVPN has many file-writing, code-loading and command-execution directives.
    // An imported .ovpn is not a trusted program. Certificates and keys must be inline.
    public static void Validate(string config)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "port", "client", "tls-client", "dev", "dev-type", "proto", "remote", "remote-random", "resolv-retry",
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
                (parts[0] == "auth-user-pass" && !OnlyCommentAfter(parts, 1)) ||
                (parts[0] == "dev" && (parts.Length < 2 || parts[1] != "tun" || !OnlyCommentAfter(parts, 2))))
            { throw new InvalidDataException("Unsafe or unsupported OpenVPN directive."); }
        }
        if (block is not null || config.Length == 0) { throw new InvalidDataException("Invalid inline OpenVPN configuration."); }
    }

    private static bool OnlyCommentAfter(string[] parts, int count) =>
        parts.Length == count || parts[count].StartsWith('#') || parts[count].StartsWith(';');
}
