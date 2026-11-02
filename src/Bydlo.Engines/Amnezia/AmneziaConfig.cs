using System.Net;
using System.Text;
using Bydlo.Core.Import.Parsers;

namespace Bydlo.Engines.Amnezia;

/// <summary>Configuration only; never executes hooks from imported wg-quick files.</summary>
public sealed record AmneziaConfig(string Uapi, string[] Addresses, string[] Dns, string[] Routes, string[] Endpoints, int Mtu)
{
    public static async Task<AmneziaConfig> ParseAsync(string raw, Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken ct)
    {
        var config = WireGuardConfig.Parse(raw);
        var iface = config.Interface;
        var allowed = new HashSet<string>(WireGuardConfig.AmneziaKeys, StringComparer.OrdinalIgnoreCase)
            { "PrivateKey", "Address", "DNS", "MTU", "ListenPort" };
        if (iface.Keys.Any(k => !allowed.Contains(k)))
        {
            throw new FormatException("Unsupported AmneziaWG interface option (hooks, Table and SaveConfig are not executed).");
        }
        var text = new StringBuilder();
        text.AppendLine("private_key=" + Key(iface["PrivateKey"]));
        if (iface.TryGetValue("ListenPort", out var listen)) { text.AppendLine("listen_port=" + Number(listen, 0, 65535)); }
        foreach (var key in WireGuardConfig.AmneziaKeys)
        {
            if (iface.TryGetValue(key, out var value)) { text.AppendLine(key.ToLowerInvariant() + "=" + value); }
        }
        text.AppendLine("replace_peers=true");
        var endpoints = new List<string>();
        var routes = new List<string>();
        if (config.Peers.Count == 0) { throw new FormatException("AmneziaWG configuration has no peers."); }
        var peerOptions = new HashSet<string>(["PublicKey", "PresharedKey", "AllowedIPs", "Endpoint", "PersistentKeepalive"], StringComparer.OrdinalIgnoreCase);
        foreach (var peer in config.Peers)
        {
            if (peer.Keys.Any(k => !peerOptions.Contains(k)))
            { throw new FormatException("Unsupported AmneziaWG peer option."); }
            text.AppendLine("public_key=" + Key(peer["PublicKey"]));
            if (peer.TryGetValue("PresharedKey", out var psk)) { text.AppendLine("preshared_key=" + Key(psk)); }
            var endpoint = peer["Endpoint"];
            if (!Uri.TryCreate("udp://" + endpoint, UriKind.Absolute, out var uri) || uri.Port is < 1 or > 65535)
            { throw new FormatException("Invalid AmneziaWG endpoint."); }
            var host = uri.Host.Trim('[', ']');
            var ip = IPAddress.TryParse(host, out var literal) ? literal : (await resolve(host, ct).ConfigureAwait(false)).First();
            endpoints.Add(ip.ToString());
            text.AppendLine("endpoint=" + new IPEndPoint(ip, uri.Port));
            if (peer.TryGetValue("PersistentKeepalive", out var keepalive))
            { text.AppendLine("persistent_keepalive_interval=" + Number(keepalive, 0, 65535)); }
            text.AppendLine("replace_allowed_ips=true");
            foreach (var cidr in List(peer.GetValueOrDefault("AllowedIPs", "0.0.0.0/0, ::/0")))
            {
                var net = Network(cidr);
                text.AppendLine("allowed_ip=" + net);
                // More-specific routes leave the original default route in place.
                routes.AddRange(net switch { "0.0.0.0/0" => ["0.0.0.0/1", "128.0.0.0/1"], "::/0" => ["::/1", "8000::/1"], _ => [net] });
            }
        }
        var addresses = List(iface.GetValueOrDefault("Address", "")).Select(Network).ToArray();
        if (addresses.Length == 0) { throw new FormatException("AmneziaWG configuration has no interface address."); }
        var dns = List(iface.GetValueOrDefault("DNS", "")).Select(v => IPAddress.Parse(v).ToString()).ToArray();
        var endpointHosts = endpoints.Select(e => e + (IPAddress.Parse(e).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128")).ToHashSet(StringComparer.Ordinal);
        return new(text.ToString(), addresses, dns, routes.Distinct().Where(r => !endpointHosts.Contains(r)).ToArray(), endpoints.Distinct().ToArray(),
            iface.TryGetValue("MTU", out var mtu) ? Number(mtu, 576, 9000) : 1420);
    }

    private static string Key(string value)
    {
        var bytes = Convert.FromBase64String(value);
        return bytes.Length == 32 ? Convert.ToHexStringLower(bytes) : throw new FormatException("Invalid AmneziaWG key length.");
    }

    private static int Number(string value, int min, int max) =>
        int.TryParse(value, out var number) && number >= min && number <= max ? number : throw new FormatException("Invalid AmneziaWG numeric option.");

    private static string[] List(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Network(string value)
    {
        var parts = value.Split('/');
        var ip = IPAddress.Parse(parts[0]);
        var max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        return ip + "/" + (parts.Length == 2 ? Number(parts[1], 0, max) : max);
    }
}
