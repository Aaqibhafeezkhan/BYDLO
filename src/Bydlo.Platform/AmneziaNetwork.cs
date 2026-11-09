using System.Net;
using System.Text.Json;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;

namespace Bydlo.Platform;

public sealed record AmneziaEndpointRoute(string Address, string Gateway, string Device);
public sealed record AmneziaNetworkLease(OsFamily Os, AmneziaEndpointRoute[] Endpoints, string? InterfaceId = null);

/// <summary>
/// Only changes a newly-created BYDLO interface. Original default routes and global DNS remain intact.
/// Durable intent precedes changes; cleanup is repeatable and retains its journal on failure.
/// All system commands go through the guarded runner (unit tests inject a fake).
/// </summary>
public sealed class AmneziaNetwork(IProcessRunner runner, OsFamily os, string directory) : IAmneziaNetwork, IAsyncDisposable
{
    public const string InterfaceName = "bydlo-awg";
    public const int RouteMetric = 42777;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileStream? _ownership;
    private string Journal => Path.Combine(directory, "amnezia-network.json");
    public bool HasPendingRecovery => File.Exists(Journal);

    public async ValueTask DisposeAsync()
    {
        try { await RemoveAsync().ConfigureAwait(false); }
        finally { Release(); _gate.Dispose(); }
        GC.SuppressFinalize(this);
    }

    public async Task PrepareAsync(IReadOnlyList<string> endpoints, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Own();
            await RemoveCoreAsync(ct).ConfigureAwait(false);
            var existing = await RunAsync(os == OsFamily.Windows
                ? Ps("@(Get-NetAdapter -Name 'bydlo-awg' -ErrorAction SilentlyContinue).Count")
                : Ip("-j", "link", "show"), ct).ConfigureAwait(false);
            if (os == OsFamily.Windows ? existing.Trim() != "0" : existing.Contains("\"bydlo-awg\"", StringComparison.Ordinal))
            { throw new InvalidOperationException("An interface named bydlo-awg already exists; refusing to change it."); }
            var bypass = new List<AmneziaEndpointRoute>();
            foreach (var endpoint in endpoints)
            {
                var ip = IPAddress.Parse(endpoint);
                var output = await RunAsync(os == OsFamily.Windows
                    ? Ps($"Find-NetRoute -RemoteIPAddress '{ip}' | Where-Object {{ $_.PSObject.Properties.Name -contains 'NextHop' }} | Select-Object -First 1 @{{n='gateway';e={{$_.NextHop}}}},@{{n='dev';e={{[string]$_.InterfaceIndex}}}} | ConvertTo-Json -Compress")
                    : Ip("-j", Family(ip), "route", "get", ip.ToString()), ct).ConfigureAwait(false);
                using var json = JsonDocument.Parse(output);
                var route = os == OsFamily.Windows ? json.RootElement : json.RootElement[0];
                var gateway = route.TryGetProperty("gateway", out var gw) ? gw.GetString()! : (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "0.0.0.0" : "::");
                var dev = route.GetProperty("dev").GetString()!;
                ValidateDevice(dev, os);
                var prefix = ip + (Family(ip) == "-4" ? "/32" : "/128");
                var collision = await RunAsync(os == OsFamily.Windows
                    ? Ps($"@(Get-NetRoute -DestinationPrefix '{prefix}' -InterfaceIndex {dev} -ErrorAction SilentlyContinue | Where-Object RouteMetric -eq {RouteMetric}).Count")
                    : Ip(Family(ip), "route", "show", prefix, "metric", RouteMetric.ToString()), ct).ConfigureAwait(false);
                if (os == OsFamily.Windows ? collision.Trim() != "0" : !string.IsNullOrWhiteSpace(collision))
                { throw new InvalidOperationException("A route using BYDLO's reserved metric already exists; refusing to change it."); }
                bypass.Add(new(ip.ToString(), IPAddress.Parse(gateway).ToString(), dev));
            }
            await SaveAsync(new(os, bypass.ToArray()), ct).ConfigureAwait(false);
        }
        catch { if (!HasPendingRecovery) { Release(); } throw; }
        finally { _gate.Release(); }
    }

    public async Task ApplyAsync(IReadOnlyList<string> addresses, IReadOnlyList<string> dns, IReadOnlyList<string> routes, int mtu, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var lease = await LoadAsync(ct).ConfigureAwait(false);
            var id = await RunAsync(os == OsFamily.Windows
                ? Ps("(Get-NetAdapter -Name 'bydlo-awg' -ErrorAction Stop).InterfaceGuid.ToString()")
                : Ip("-j", "link", "show", "dev", InterfaceName), ct).ConfigureAwait(false);
            if (os == OsFamily.Windows) { id = Guid.Parse(id.Trim()).ToString(); }
            else { using var json = JsonDocument.Parse(id); id = json.RootElement[0].GetProperty("ifindex").GetInt32().ToString(); }
            lease = lease with { InterfaceId = id };
            await SaveAsync(lease, ct).ConfigureAwait(false);
            // Pin outer transport before installing any tunnel routes.
            foreach (var endpoint in lease.Endpoints)
            {
                var ip = IPAddress.Parse(endpoint.Address);
                var prefix = ip + (Family(ip) == "-4" ? "/32" : "/128");
                await RunAsync(os == OsFamily.Windows
                    ? Ps($"New-NetRoute -DestinationPrefix '{prefix}' -InterfaceIndex {endpoint.Device} -NextHop '{endpoint.Gateway}' -RouteMetric {RouteMetric} -PolicyStore ActiveStore -ErrorAction Stop | Out-Null")
                    : Ip(BypassArguments(endpoint, "add")), ct).ConfigureAwait(false);
            }
            foreach (var address in addresses)
            {
                var parts = address.Split('/');
                var host = IPAddress.Parse(parts[0]);
                var prefixLength = int.Parse(parts[1]);
                await RunAsync(os == OsFamily.Windows
                    ? Ps($"New-NetIPAddress -InterfaceAlias 'bydlo-awg' -IPAddress '{host}' -PrefixLength {prefixLength} -PolicyStore ActiveStore -ErrorAction Stop | Out-Null")
                    : Ip(Family(host), "address", "add", address, "dev", InterfaceName), ct).ConfigureAwait(false);
            }
            await RunAsync(os == OsFamily.Windows
                ? Ps($"Set-NetIPInterface -InterfaceAlias 'bydlo-awg' -NlMtuBytes {mtu} -ErrorAction Stop")
                : Ip("link", "set", "dev", InterfaceName, "mtu", mtu.ToString(), "up"), ct).ConfigureAwait(false);
            foreach (var route in routes)
            {
                var network = IPNetwork.Parse(route);
                await RunAsync(os == OsFamily.Windows
                    ? Ps($"New-NetRoute -DestinationPrefix '{network}' -InterfaceAlias 'bydlo-awg' -RouteMetric 5 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null")
                    : Ip(Family(network.BaseAddress), "route", "add", network.ToString(), "dev", InterfaceName, "metric", RouteMetric.ToString()), ct).ConfigureAwait(false);
            }
            if (dns.Count > 0)
            {
                var servers = dns.Select(v => IPAddress.Parse(v).ToString()).ToArray();
                if (os == OsFamily.Windows)
                {
                    await RunAsync(Ps("Set-DnsClientServerAddress -InterfaceAlias 'bydlo-awg' -ServerAddresses " + string.Join(',', servers.Select(s => "'" + s + "'")) + " -ErrorAction Stop"), ct).ConfigureAwait(false);
                }
                else
                {
                    await RunAsync(new ProcessSpec { FileName = "resolvectl", Arguments = ["dns", InterfaceName, .. servers], DisplayName = "amnezia-network" }, ct).ConfigureAwait(false);
                    await RunAsync(new ProcessSpec { FileName = "resolvectl", Arguments = ["domain", InterfaceName, "~."], DisplayName = "amnezia-network" }, ct).ConfigureAwait(false);
                }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { if (HasPendingRecovery) { Own(); await RemoveCoreAsync(ct).ConfigureAwait(false); } Release(); }
        finally { _gate.Release(); }
    }

    private async Task RemoveCoreAsync(CancellationToken ct)
    {
        if (!HasPendingRecovery) { return; }
        var lease = await LoadAsync(ct).ConfigureAwait(false);
        foreach (var endpoint in lease.Endpoints)
        {
            var ip = IPAddress.Parse(endpoint.Address);
            var prefix = ip + (Family(ip) == "-4" ? "/32" : "/128");
            if (os == OsFamily.Windows)
            {
                await RunAsync(Ps($"Get-NetRoute -DestinationPrefix '{prefix}' -InterfaceIndex {endpoint.Device} -ErrorAction SilentlyContinue | Where-Object {{ $_.NextHop -eq '{endpoint.Gateway}' -and $_.RouteMetric -eq {RouteMetric} }} | Remove-NetRoute -Confirm:$false -ErrorAction Stop"), ct).ConfigureAwait(false);
            }
            else
            {
                // Query before removal: absence is success; a failed removal is not silently ignored.
                var found = await RunAsync(Ip(Family(ip), "route", "show", prefix, "metric", RouteMetric.ToString()), ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(found)) { await RunAsync(Ip(BypassArguments(endpoint, "del")), ct).ConfigureAwait(false); }
            }
        }
        if (lease.InterfaceId is { } id)
        {
            if (os == OsFamily.Windows)
            {
                var guid = Guid.Parse(id);
                await RunAsync(Ps($"$a=Get-NetAdapter -Name 'bydlo-awg' -ErrorAction SilentlyContinue; if ($a -and $a.InterfaceGuid -eq [guid]'{guid}') {{ Get-NetRoute -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue | Where-Object RouteMetric -eq 5 | Remove-NetRoute -Confirm:$false -ErrorAction Stop; Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ResetServerAddresses -ErrorAction Stop }}"), ct).ConfigureAwait(false);
            }
            else
            {
                var links = await RunAsync(Ip("-j", "link", "show"), ct).ConfigureAwait(false);
                using var json = JsonDocument.Parse(links);
                if (json.RootElement.EnumerateArray().Any(e => e.GetProperty("ifname").GetString() == InterfaceName && e.GetProperty("ifindex").GetInt32().ToString() == id))
                { await RunAsync(Ip("link", "delete", "dev", InterfaceName), ct).ConfigureAwait(false); }
            }
        }
        File.Delete(Journal);
    }

    private async Task<AmneziaNetworkLease> LoadAsync(CancellationToken ct)
    {
        var lease = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Journal, ct).ConfigureAwait(false), PlatformJsonContext.Default.AmneziaNetworkLease)
            ?? throw new InvalidDataException("Invalid AmneziaWG recovery journal.");
        if (lease.Os != os) { throw new InvalidDataException("Recovery journal belongs to another platform."); }
        foreach (var e in lease.Endpoints) { _ = IPAddress.Parse(e.Address); _ = IPAddress.Parse(e.Gateway); ValidateDevice(e.Device, os); }
        return lease;
    }

    private async Task SaveAsync(AmneziaNetworkLease lease, CancellationToken ct)
    {
        var temp = Journal + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(lease, PlatformJsonContext.Default.AmneziaNetworkLease), ct).ConfigureAwait(false);
        File.Move(temp, Journal, overwrite: true);
    }

    private void Own()
    {
        Directory.CreateDirectory(directory);
        _ownership ??= new FileStream(Path.Combine(directory, "amnezia-network.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private void Release() { _ownership?.Dispose(); _ownership = null; }
    private static void ValidateDevice(string name, OsFamily family)
    {
        if (family == OsFamily.Windows ? !int.TryParse(name, out var index) || index <= 0 : name.Length == 0 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
        { throw new InvalidDataException("Invalid route interface."); }
    }
    private static string Family(IPAddress ip) => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "-4" : "-6";
    private static string[] BypassArguments(AmneziaEndpointRoute route, string action)
    {
        var ip = IPAddress.Parse(route.Address);
        var args = new List<string> { Family(ip), "route", action, ip + (Family(ip) == "-4" ? "/32" : "/128") };
        if (route.Gateway is not ("0.0.0.0" or "::")) { args.AddRange(["via", route.Gateway]); }
        args.AddRange(["dev", route.Device, "metric", RouteMetric.ToString()]);
        return args.ToArray();
    }
    private static ProcessSpec Ip(params string[] args) => new() { FileName = "ip", Arguments = args, DisplayName = "amnezia-network" };
    private static ProcessSpec Ps(string script) => new()
    {
        FileName = "powershell.exe", Arguments = ["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; " + script], DisplayName = "amnezia-network",
    };
    private async Task<string> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        var (code, output) = await runner.RunToCompletionAsync(spec, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (code != 0) { throw new InvalidOperationException("AmneziaWG network configuration failed; recovery journal was retained."); }
        return output;
    }
}
