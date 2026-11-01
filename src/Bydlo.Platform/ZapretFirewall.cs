using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;

namespace Bydlo.Platform;

public sealed record ZapretFirewallLease(string Id, OsFamily Os, string? PfToken = null);

/// <summary>Pure rule generation. No global flush, default-policy changes, or edits to pf.conf.</summary>
public static partial class ZapretFirewallRules
{
    public static string Scope(ZapretFirewallLease lease)
    {
        if (!Guid.TryParseExact(lease.Id, "N", out _))
        {
            throw new InvalidDataException("Invalid Zapret firewall scope.");
        }

        return lease.Os == OsFamily.Linux ? "bydlo_" + lease.Id : "com.apple/bydlo_" + lease.Id;
    }

    public static string Build(ZapretFirewallLease lease, string tcpPorts, string udpPorts, IReadOnlyList<string> excludedNetworks)
    {
        ValidatePorts(tcpPorts);
        ValidatePorts(udpPorts);
        var excluded = excludedNetworks.Select(ValidateNetwork).ToArray();
        var scope = Scope(lease);
        if (lease.Os == OsFamily.Linux)
        {
            var exclusions = string.Join("\n", excluded.Select(n => $"    {(n.Contains(':') ? "ip6" : "ip")} daddr {n} return"));
            return $$"""
                table inet {{scope}} {
                  chain output {
                    type filter hook output priority mangle; policy accept;
                    meta mark & 0x40000000 != 0 return
                    oifname "lo" return
                    ip daddr { 0.0.0.0/8, 10.0.0.0/8, 127.0.0.0/8, 169.254.0.0/16, 172.16.0.0/12, 192.168.0.0/16, 224.0.0.0/4 } return
                    ip6 daddr { ::1, fc00::/7, fe80::/10, ff00::/8 } return
                {{exclusions}}
                    tcp dport { {{tcpPorts}} } queue num 21770 bypass
                    udp dport { {{udpPorts}} } queue num 21770 bypass
                  }
                }
                """ + "\n";
        }

        if (lease.Os != OsFamily.MacOS)
        {
            throw new PlatformNotSupportedException("No native Zapret firewall on this platform.");
        }

        // IPv4 only: IPv6 redirection needs additional interface/address setup, outside this scope.
        var ipv4 = excluded.Where(n => !n.Contains(':'));
        return $$"""
            table <excluded> const { 0.0.0.0/8, 10.0.0.0/8, 127.0.0.0/8, 169.254.0.0/16, 172.16.0.0/12, 192.168.0.0/16, 224.0.0.0/4{{string.Concat(ipv4.Select(n => ", " + n))}} }
            rdr pass on lo0 inet proto tcp from !127.0.0.0/8 to !<excluded> port { 80,443 } -> 127.0.0.1 port 21771
            pass out route-to (lo0 127.0.0.1) inet proto tcp from any to !<excluded> port { 80,443 } user > root
            """ + "\n";
    }

    private static void ValidatePorts(string ports)
    {
        if (!Ports().IsMatch(ports) || ports.Split(',', '-').Any(p => !int.TryParse(p, out var n) || n is < 1 or > 65535))
        {
            throw new FormatException("Invalid Zapret port range.");
        }
    }

    private static string ValidateNetwork(string value)
    {
        var parts = value.Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip) ||
            (parts.Length == 2 && (!int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))))
        {
            throw new FormatException("Invalid IP exclusion in Zapret user list.");
        }

        return ip + (parts.Length == 2 ? "/" + int.Parse(parts[1]) : "");
    }

    [GeneratedRegex(@"^\d+(?:-\d+)?(?:,\d+(?:-\d+)?)*$")]
    private static partial Regex Ports();
}

/// <summary>
/// Privileged operations are executed through a guarded process runner. Tests inject a fake runner.
/// The durable journal contains only a typed scope/token, never arbitrary commands to replay as root.
/// </summary>
public sealed class ZapretFirewall(IProcessRunner runner, string dataDirectory) : IZapretFirewall, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileStream? _ownership;
    private string JournalPath => Path.Combine(dataDirectory, "firewall.json");
    private string RulesPath => Path.Combine(dataDirectory, "firewall.rules");

    public async ValueTask DisposeAsync()
    {
        try { await RemoveAsync().ConfigureAwait(false); }
        finally { ReleaseOwnership(); _gate.Dispose(); }
        GC.SuppressFinalize(this);
    }

    public bool HasPendingRecovery => File.Exists(JournalPath);

    public async Task ApplyAsync(OsFamily os, string tcpPorts, string udpPorts, IReadOnlyList<string> excludedNetworks, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AcquireOwnership();
            await RemoveUnsafeAsync().ConfigureAwait(false);
            AcquireOwnership();
            var lease = new ZapretFirewallLease(Guid.NewGuid().ToString("N"), os);
            var rules = ZapretFirewallRules.Build(lease, tcpPorts, udpPorts, excludedNetworks);
            await File.WriteAllTextAsync(RulesPath, rules, ct).ConfigureAwait(false);
            if (os == OsFamily.MacOS)
            {
                // The default macOS anchors are required. Do not rewrite the user's root ruleset.
                var nat = await RunAsync(os, ["-s", "nat"], ct).ConfigureAwait(false);
                var filter = await RunAsync(os, ["-s", "rules"], ct).ConfigureAwait(false);
                if (!nat.Contains("rdr-anchor \"com.apple/*\"", StringComparison.Ordinal) || !filter.Contains("anchor \"com.apple/*\"", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("macOS PF needs the standard com.apple/* filter and rdr anchors in the active ruleset.");
                }

                await RunAsync(os, ["-n", "-a", ZapretFirewallRules.Scope(lease), "-f", RulesPath], ct).ConfigureAwait(false);
            }
            else
            {
                await RunAsync(os, ["-c", "-f", RulesPath], ct).ConfigureAwait(false);
            }

            // Persist intent before any firewall mutation, including a partially failed apply.
            Save(lease);
            await RunAsync(os, os == OsFamily.Linux ? ["-f", RulesPath] : ["-a", ZapretFirewallRules.Scope(lease), "-f", RulesPath], ct).ConfigureAwait(false);
            if (os == OsFamily.MacOS)
            {
                // Reference-counted enable: never globally disable PF on stop.
                var output = await RunAsync(os, ["-E"], ct).ConfigureAwait(false);
                var token = Regex.Match(output, @"Token\s*:\s*(\d+)");
                if (!token.Success)
                {
                    throw new InvalidOperationException("PF did not return an enable token; firewall cleanup is required.");
                }

                Save(lease with { PfToken = token.Groups[1].Value });
            }
        }
        catch
        {
            try { if (_ownership is not null) { await RemoveUnsafeAsync().ConfigureAwait(false); } }
            finally { ReleaseOwnership(); }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            AcquireOwnership();
            await RemoveUnsafeAsync().ConfigureAwait(false);
        }
        finally
        {
            ReleaseOwnership();
            _gate.Release();
        }
    }

    private async Task RemoveUnsafeAsync()
    {
        if (!File.Exists(JournalPath))
        {
            return;
        }

        var lease = JsonSerializer.Deserialize<ZapretFirewallLease>(await File.ReadAllTextAsync(JournalPath).ConfigureAwait(false))
            ?? throw new InvalidDataException("Zapret firewall journal is invalid; refusing to discard it.");
        var scope = ZapretFirewallRules.Scope(lease);
        if (lease.Os == OsFamily.Linux)
        {
            // Query first so cleanup is idempotent after reboot or a crash after deletion.
            var result = await runner.RunToCompletionAsync(Spec(lease.Os, ["list", "tables"]), TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException("Could not inspect nftables for Zapret recovery.");
            }

            if (result.Output.Split('\n').Any(l => l.Trim() == "table inet " + scope))
            {
                await RunAsync(lease.Os, ["delete", "table", "inet", scope], CancellationToken.None).ConfigureAwait(false);
            }
        }
        else if (lease.Os == OsFamily.MacOS)
        {
            // Never use -F all: some PF implementations flush global states even with -a.
            await RunAsync(lease.Os, ["-a", scope, "-F", "rules"], CancellationToken.None).ConfigureAwait(false);
            await RunAsync(lease.Os, ["-a", scope, "-F", "nat"], CancellationToken.None).ConfigureAwait(false);
            if (lease.PfToken is { } token)
            {
                if (!Regex.IsMatch(token, @"^\d+$"))
                {
                    throw new InvalidDataException("Invalid PF reference token.");
                }

                var status = await RunAsync(lease.Os, ["-s", "info"], CancellationToken.None).ConfigureAwait(false);
                // A reboot clears reference tokens. Do not fail recovery when PF is already disabled.
                if (!Regex.IsMatch(status, @"Status:\s*Disabled", RegexOptions.IgnoreCase))
                {
                    await RunAsync(lease.Os, ["-X", token], CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        else
        {
            throw new InvalidDataException("Invalid platform in Zapret firewall journal.");
        }

        File.Delete(JournalPath);
        File.Delete(RulesPath);
    }

    private void AcquireOwnership()
    {
        if (_ownership is not null) { return; }
        Directory.CreateDirectory(dataDirectory);
        _ownership = new FileStream(Path.Combine(dataDirectory, "firewall.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private void ReleaseOwnership()
    {
        _ownership?.Dispose();
        _ownership = null;
    }

    private void Save(ZapretFirewallLease lease)
    {
        var temp = JournalPath + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, lease);
            file.Flush(flushToDisk: true);
        }

        File.Move(temp, JournalPath, overwrite: true);
    }

    private static ProcessSpec Spec(OsFamily os, IReadOnlyList<string> args) => new()
    {
        FileName = os == OsFamily.MacOS ? "/sbin/pfctl" : File.Exists("/usr/sbin/nft") ? "/usr/sbin/nft" : "/sbin/nft",
        Arguments = args,
        DisplayName = "Zapret firewall",
        AffectsNetwork = true,
    };

    private async Task<string> RunAsync(OsFamily os, IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await runner.RunToCompletionAsync(Spec(os, args), TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Zapret firewall operation failed (exit {result.ExitCode}): {result.Output}");
        }

        return result.Output;
    }
}
