using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Platform;

#pragma warning disable CA1001 // The lease is retained until tunnel cleanup.
internal sealed class MacAmneziaNetwork(IProcessRunner runner, string directory, Action? checkSafety = null) : IAmneziaNetwork
#pragma warning restore CA1001
{
    private string Journal => Path.Combine(directory, "amnezia-macos-network.json");
    private FileStream? _lease;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string TunnelName => "utun";
    public bool HasPendingRecovery => File.Exists(Journal);

    public async Task PrepareAsync(IReadOnlyList<string> endpoints, CancellationToken ct)
    {
        EnsureAllowed();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Acquire();
            await RestoreAsync(ct).ConfigureAwait(false);
            var bypasses = new List<Endpoint>();
            foreach (var value in endpoints.Distinct())
            {
                var ip = IPAddress.Parse(value);
                var current = await LookupAsync(ip, ct).ConfigureAwait(false);
                if (current is null)
                {
                    throw new IOException("No route to the AmneziaWG peer.");
                }
                if (current.Flags.Contains("HOST", StringComparison.Ordinal) && current.Destination == ip.ToString())
                {
                    continue; // Retain an existing exact route owned by the OS or another VPN.
                }
                var endpoint = new Endpoint(ip.ToString(), current.Interface, current.Gateway);
                ValidateEndpoint(endpoint);
                bypasses.Add(endpoint);
            }
            await SaveAsync(new Lease(bypasses.ToArray(), []), ct).ConfigureAwait(false);
        }
        catch
        {
            // Failed recovery still belongs to this session until cleanup is retried.
            if (!HasPendingRecovery) { Release(); }
            throw;
        }
        finally { _gate.Release(); }
    }

    public Task ApplyAsync(IReadOnlyList<string> addresses, IReadOnlyList<string> dns, IReadOnlyList<string> routes,
        int mtu, CancellationToken ct) => throw new InvalidOperationException("The utun interface is required.");

    public async Task ApplyToInterfaceAsync(string? name, IReadOnlyList<string> addresses, IReadOnlyList<string> dns,
        IReadOnlyList<string> routes, int mtu, CancellationToken ct)
    {
        EnsureAllowed();
        if (name is null || !name.StartsWith("utun", StringComparison.Ordinal) ||
            !int.TryParse(name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new InvalidDataException("Invalid AmneziaWG interface.");
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_lease is null) { throw new InvalidOperationException("No prepared AmneziaWG session."); }
            var lease = await LoadAsync(ct).ConfigureAwait(false);
            var dnsChanges = await CaptureDnsAsync(dns, ct).ConfigureAwait(false);
            await SaveAsync(lease with { Dns = dnsChanges }, ct).ConfigureAwait(false);

            foreach (var endpoint in lease.Endpoints)
            {
                await ChangeEndpointAsync("add", endpoint, ct).ConfigureAwait(false);
            }
            foreach (var value in addresses)
            {
                var parts = value.Split('/', 2);
                var ip = IPAddress.Parse(parts[0]);
                var prefix = int.Parse(parts[1], CultureInfo.InvariantCulture);
                if (Family(ip) == "-inet")
                {
                    var mask = prefix == 0 ? 0U : uint.MaxValue << (32 - prefix);
                    var maskText = new IPAddress(new byte[] { (byte)(mask >> 24), (byte)(mask >> 16), (byte)(mask >> 8), (byte)mask }).ToString();
                    await RunAsync("ifconfig", true, ct, name, "inet", ip.ToString(), ip.ToString(), "netmask", maskText, "alias")
                        .ConfigureAwait(false);
                }
                else
                {
                    await RunAsync("ifconfig", true, ct, name, "inet6", ip.ToString(), "prefixlen", prefix.ToString(CultureInfo.InvariantCulture), "alias")
                        .ConfigureAwait(false);
                }
            }
            await RunAsync("ifconfig", true, ct, name, "mtu", mtu.ToString(CultureInfo.InvariantCulture), "up").ConfigureAwait(false);
            foreach (var route in routes.SelectMany(TunnelRoutes).Distinct())
            {
                var network = IPNetwork.Parse(route);
                await RunAsync("route", true, ct, "-n", "add", Family(network.BaseAddress), "-net", network.ToString(), "-interface", name)
                    .ConfigureAwait(false);
            }
            foreach (var change in dnsChanges)
            {
                await ChangeDnsAsync(change.Service, change.Applied, ct).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(CancellationToken ct = default)
    {
        if (!HasPendingRecovery)
        {
            return;
        }
        EnsureAllowed();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Acquire();
            await RestoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Release();
            _gate.Release();
        }
    }

    private async Task RestoreAsync(CancellationToken ct)
    {
        if (!HasPendingRecovery) { return; }
        var lease = await LoadAsync(ct).ConfigureAwait(false);
        foreach (var change in lease.Dns)
        {
            if ((await ReadDnsAsync(change.Service, ct).ConfigureAwait(false)).SequenceEqual(change.Applied))
            {
                await ChangeDnsAsync(change.Service, change.Previous, ct).ConfigureAwait(false);
            }
        }
        foreach (var endpoint in lease.Endpoints)
        {
            var current = await LookupAsync(IPAddress.Parse(endpoint.Address), ct).ConfigureAwait(false);
            if (current is not null && current.Flags.Contains("HOST", StringComparison.Ordinal)
                && current.Flags.Contains("PROTO2", StringComparison.Ordinal) && current.Destination == endpoint.Address
                && current.Interface == endpoint.Interface
                && (endpoint.Gateway.StartsWith("link#", StringComparison.Ordinal) || current.Gateway == endpoint.Gateway))
            {
                await ChangeEndpointAsync("delete", endpoint, ct).ConfigureAwait(false);
            }
        }
        File.Delete(Journal);
    }

    private async Task<DnsChange[]> CaptureDnsAsync(IReadOnlyList<string> dns, CancellationToken ct)
    {
        if (dns.Count == 0) { return []; }
        var applied = dns.Select(value => IPAddress.Parse(value).ToString()).ToArray();
        var network = await RunAsync("scutil", false, ct, "--nwi").ConfigureAwait(false);
        var order = await RunAsync("networksetup", false, ct, "-listnetworkserviceorder").ConfigureAwait(false);
        var changes = new List<DnsChange>();
        foreach (var service in MacNetworkServices.Active(network, order))
        {
            changes.Add(new(service, await ReadDnsAsync(service, ct).ConfigureAwait(false), applied));
        }
        if (changes.Count == 0) { throw new IOException("No active macOS DNS service."); }
        return changes.ToArray();
    }

    private async Task<string[]> ReadDnsAsync(string service, CancellationToken ct)
    {
        ValidateService(service);
        var output = await RunAsync("networksetup", false, ct, "-getdnsservers", service).ConfigureAwait(false);
        if (output.StartsWith("There aren't any DNS Servers set", StringComparison.Ordinal)) { return []; }
        return output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => IPAddress.Parse(value).ToString()).ToArray();
    }

    private Task<string> ChangeDnsAsync(string service, string[] servers, CancellationToken ct)
    {
        ValidateService(service);
        return RunAsync("networksetup", true, ct,
            ["-setdnsservers", service, .. (servers.Length == 0 ? ["Empty"] : servers.Select(value => IPAddress.Parse(value).ToString()).ToArray())]);
    }

    private async Task<RouteInfo?> LookupAsync(IPAddress ip, CancellationToken ct)
    {
        var (code, output) = await runner.RunToCompletionAsync(Spec("route", false, "-n", "get", Family(ip), ip.ToString()),
            TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (code != 0)
        {
            if (output.Contains("not in table", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            throw new IOException("Could not read a macOS route.");
        }

        var fields = output.Split('\n').Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
        return new RouteInfo(fields.GetValueOrDefault("destination") ?? "", fields.GetValueOrDefault("interface") ?? "",
            fields.GetValueOrDefault("gateway") ?? "", fields.GetValueOrDefault("flags") ?? "");
    }

    private Task<string> ChangeEndpointAsync(string operation, Endpoint endpoint, CancellationToken ct)
    {
        ValidateEndpoint(endpoint);
        var ip = IPAddress.Parse(endpoint.Address);
        return endpoint.Gateway.StartsWith("link#", StringComparison.Ordinal)
            ? RunAsync("route", true, ct, "-n", operation, Family(ip), "-host", endpoint.Address, "-interface", endpoint.Interface, "-proto2")
            : RunAsync("route", true, ct, "-n", operation, Family(ip), "-host", endpoint.Address, endpoint.Gateway, "-proto2");
    }

    private static IEnumerable<string> TunnelRoutes(string value)
    {
        var network = IPNetwork.Parse(value);
        if (network.PrefixLength != 0) { return [network.ToString()]; }
        return Family(network.BaseAddress) == "-inet" ? ["0.0.0.0/1", "128.0.0.0/1"] : ["::/1", "8000::/1"];
    }

    private static string Family(IPAddress ip) => ip.AddressFamily == AddressFamily.InterNetwork ? "-inet" : "-inet6";

    private static void ValidateEndpoint(Endpoint endpoint)
    {
        _ = IPAddress.Parse(endpoint.Address);
        var gateway = endpoint.Gateway.Split('%', 2);
        if (endpoint.Interface.Length == 0 || !endpoint.Interface.All(char.IsAsciiLetterOrDigit)
            || (gateway.Length > 1 && gateway[1] != endpoint.Interface)
            || (!endpoint.Gateway.StartsWith("link#", StringComparison.Ordinal) && !IPAddress.TryParse(gateway[0], out _)))
        {
            throw new InvalidDataException("Invalid macOS endpoint route.");
        }
    }

    private static void ValidateService(string service)
    {
        if (string.IsNullOrEmpty(service) || service.StartsWith('-') || service.Any(char.IsControl))
        {
            throw new InvalidDataException("Invalid macOS service name.");
        }
    }

    private async Task<Lease> LoadAsync(CancellationToken ct) =>
        JsonSerializer.Deserialize<Lease>(await File.ReadAllTextAsync(Journal, ct).ConfigureAwait(false))
            ?? throw new InvalidDataException("Invalid AmneziaWG recovery journal.");

    private async Task SaveAsync(Lease lease, CancellationToken ct)
    {
        var temp = Journal + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(lease), ct).ConfigureAwait(false);
        File.Move(temp, Journal, overwrite: true);
    }

    private async Task<string> RunAsync(string file, bool write, CancellationToken ct, params string[] args)
    {
        var (code, output) = await runner.RunToCompletionAsync(Spec(file, write, args), TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (code != 0) { throw new IOException("AmneziaWG macOS network configuration failed."); }
        return output;
    }

    private static ProcessSpec Spec(string file, bool write, params string[] args) => new()
    {
        FileName = file, Arguments = args, AffectsNetwork = write, DisplayName = "amnezia-network",
        Environment = new Dictionary<string, string> { ["LC_ALL"] = "C" },
    };

    private void Acquire()
    {
        Directory.CreateDirectory(directory);
        _lease ??= new FileStream(Path.Combine(directory, "amnezia-macos-network.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private void Release()
    {
        _lease?.Dispose();
        _lease = null;
    }

    private void EnsureAllowed()
    {
        if (checkSafety is not null) { checkSafety(); }
        else { NetworkSafetyGuard.EnsureAllowed("configure an AmneziaWG interface"); }
    }

    private sealed record Endpoint(string Address, string Interface, string Gateway);
    private sealed record DnsChange(string Service, string[] Previous, string[] Applied);
    private sealed record Lease(Endpoint[] Endpoints, DnsChange[] Dns);
    private sealed record RouteInfo(string Destination, string Interface, string Gateway, string Flags);
}
