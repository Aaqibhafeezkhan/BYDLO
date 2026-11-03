using System.Globalization;
using System.Text.Json;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Platform;

internal enum DesktopProxyKind { Gnome, Kde5, Kde6, MacOs }

#pragma warning disable CA1001 // The lease is retained while the proxy is installed and released by ClearAsync.
internal sealed class JournaledSystemProxyService(IProcessRunner runner, DesktopProxyKind kind, string directory,
    Action? checkSafety = null) : ISystemProxyService
#pragma warning restore CA1001
{
    private const string Missing = "bydlo-value-not-present";
    private const string GnomeSchema = "org.gnome.system.proxy";
    private string Journal => Path.Combine(directory, "system-proxy.json");
    private FileStream? _lease;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsSupported => true;

    public async Task SetAsync(string host, int port, CancellationToken ct = default)
    {
        EnsureAllowed();
        if (host != "127.0.0.1" || port is < 1 or > 65535)
        {
            throw new ArgumentException("Only a valid loopback proxy is supported.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Acquire();
            await RestoreAsync(ct).ConfigureAwait(false);
            var changes = await CaptureAsync(host, port, ct).ConfigureAwait(false);
            var temporary = Journal + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new ProxyJournal(kind, changes)), ct).ConfigureAwait(false);
            File.Move(temporary, Journal, overwrite: true);
            foreach (var change in changes)
            {
                await WriteAsync(change.Scope, change.Key, change.Applied, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            if (_lease is not null)
            {
                try { await RestoreAsync(CancellationToken.None).ConfigureAwait(false); }
                finally { Release(); }
            }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        if (!File.Exists(Journal))
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
        if (!File.Exists(Journal))
        {
            return;
        }
        ProxyJournal? journal;
        await using (var stream = File.OpenRead(Journal))
        {
            journal = await JsonSerializer.DeserializeAsync<ProxyJournal>(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        if (journal?.Kind != kind || journal.Changes is null)
        {
            throw new InvalidDataException("Proxy recovery belongs to another desktop environment.");
        }

        foreach (var change in journal.Changes.Reverse())
        {
            if (await ReadAsync(change.Scope, change.Key, ct).ConfigureAwait(false) == change.Applied)
            {
                await WriteAsync(change.Scope, change.Key, change.Previous, ct).ConfigureAwait(false);
            }
        }
        File.Delete(Journal);
    }

    private async Task<ProxyChange[]> CaptureAsync(string host, int port, CancellationToken ct)
    {
        var portText = port.ToString(CultureInfo.InvariantCulture);
        var updates = kind switch
        {
            DesktopProxyKind.Gnome => new (string Scope, string Key, string Value)[]
            {
                (GnomeSchema + ".http", "use-authentication", "false"),
                (GnomeSchema + ".http", "host", "'" + host + "'"),
                (GnomeSchema + ".http", "port", portText),
                (GnomeSchema + ".https", "host", "'" + host + "'"),
                (GnomeSchema + ".https", "port", portText),
                (GnomeSchema, "mode", "'manual'"),
            },
            DesktopProxyKind.Kde5 or DesktopProxyKind.Kde6 =>
            [
                ("Proxy Settings", "httpProxy", $"http://{host} {portText}"),
                ("Proxy Settings", "httpsProxy", $"http://{host} {portText}"),
                ("Proxy Settings", "ProxyType", "1"),
            ],
            _ => await MacUpdatesAsync(host, port, ct).ConfigureAwait(false),
        };

        var result = new List<ProxyChange>();
        foreach (var (scope, key, value) in updates)
        {
            var previous = await ReadAsync(scope, key, ct).ConfigureAwait(false);
            if (kind == DesktopProxyKind.MacOs && JsonSerializer.Deserialize<MacProxy>(previous)?.Authenticated == true)
            {
                throw new InvalidOperationException("An authenticated system proxy cannot be restored automatically.");
            }
            result.Add(new(scope, key, previous, value));
        }
        return result.ToArray();
    }

    private async Task<(string Scope, string Key, string Value)[]> MacUpdatesAsync(string host, int port, CancellationToken ct)
    {
        var network = await RunAsync("scutil", false, ct, "--nwi").ConfigureAwait(false);
        var services = await RunAsync("networksetup", false, ct, "-listnetworkserviceorder").ConfigureAwait(false);
        var active = MacNetworkServices.Active(network, services);
        if (active.Count == 0)
        {
            throw new IOException("No active macOS network services are available.");
        }
        var settings = JsonSerializer.Serialize(new MacProxy(true, host, port, false));
        return active.SelectMany(service => new[] { (service, "web", settings), (service, "secureweb", settings) }).ToArray();
    }

    private async Task<string> ReadAsync(string scope, string key, CancellationToken ct)
    {
        ValidateSlot(scope, key);
        if (kind == DesktopProxyKind.Gnome)
        {
            return await RunAsync("gsettings", false, ct, "get", scope, key).ConfigureAwait(false);
        }
        if (kind != DesktopProxyKind.MacOs)
        {
            return await RunAsync("kreadconfig" + KdeSuffix, false, ct, "--file", "kioslaverc",
                "--group", scope, "--key", key, "--default", Missing).ConfigureAwait(false);
        }

        var output = await RunAsync("networksetup", false, ct, "-get" + key + "proxy", scope).ConfigureAwait(false);
        var fields = output.Split('\n').Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
        if (!fields.TryGetValue("Enabled", out var enabled) || enabled is not ("Yes" or "No") ||
            !int.TryParse(fields.GetValueOrDefault("Port"), CultureInfo.InvariantCulture, out var port))
        {
            throw new InvalidDataException("Unexpected networksetup proxy settings.");
        }
        return JsonSerializer.Serialize(new MacProxy(enabled == "Yes", fields.GetValueOrDefault("Server") ?? "", port,
            fields.GetValueOrDefault("Authenticated Proxy Enabled") is "1" or "Yes"));
    }

    private async Task WriteAsync(string scope, string key, string value, CancellationToken ct)
    {
        ValidateSlot(scope, key);
        if (kind == DesktopProxyKind.Gnome)
        {
            await RunAsync("gsettings", true, ct, "set", scope, key, value).ConfigureAwait(false);
            return;
        }
        if (kind != DesktopProxyKind.MacOs)
        {
            var arguments = new List<string> { "--file", "kioslaverc", "--group", scope, "--key", key, "--notify" };
            if (value == Missing) { arguments.Add("--delete"); }
            else { arguments.AddRange(["--", value]); }
            await RunAsync("kwriteconfig" + KdeSuffix, true, ct, [.. arguments]).ConfigureAwait(false);
            return;
        }

        var proxy = JsonSerializer.Deserialize<MacProxy>(value) ?? throw new InvalidDataException("Invalid proxy snapshot.");
        if (proxy.Authenticated || proxy.Port is < 0 or > 65535 ||
            (!string.IsNullOrEmpty(proxy.Host) && Uri.CheckHostName(proxy.Host) == UriHostNameType.Unknown))
        {
            throw new InvalidDataException("Invalid networksetup proxy settings.");
        }
        if (proxy.Port != 0 && !string.IsNullOrEmpty(proxy.Host))
        {
            await RunAsync("networksetup", true, ct, "-set" + key + "proxy", scope, proxy.Host,
                proxy.Port.ToString(CultureInfo.InvariantCulture), "off").ConfigureAwait(false);
        }
        await RunAsync("networksetup", true, ct, "-set" + key + "proxystate", scope, proxy.Enabled ? "on" : "off").ConfigureAwait(false);
    }

    private void ValidateSlot(string scope, string key)
    {
        var valid = kind switch
        {
            DesktopProxyKind.Gnome => (scope == GnomeSchema && key == "mode") ||
                (scope is GnomeSchema + ".http" or GnomeSchema + ".https" && key is "host" or "port") ||
                (scope == GnomeSchema + ".http" && key == "use-authentication"),
            DesktopProxyKind.Kde5 or DesktopProxyKind.Kde6 =>
                scope == "Proxy Settings" && key is "httpProxy" or "httpsProxy" or "ProxyType",
            _ => key is "web" or "secureweb" && !string.IsNullOrEmpty(scope) && !scope.StartsWith('-') && !scope.Any(char.IsControl),
        };
        if (!valid) { throw new InvalidDataException("Invalid proxy recovery entry."); }
    }

    private async Task<string> RunAsync(string file, bool write, CancellationToken ct, params string[] args)
    {
        var (exitCode, output) = await runner.RunToCompletionAsync(new ProcessSpec
        {
            FileName = file, Arguments = args, AffectsNetwork = write, DisplayName = "system-proxy",
            Environment = new Dictionary<string, string> { ["LC_ALL"] = "C" },
        }, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new IOException("The desktop proxy command failed.");
        }
        return output.Trim();
    }

    private string KdeSuffix => kind == DesktopProxyKind.Kde6 ? "6" : "5";

    private void Acquire()
    {
        Directory.CreateDirectory(directory);
        _lease ??= new FileStream(Path.Combine(directory, "system-proxy.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private void Release()
    {
        _lease?.Dispose();
        _lease = null;
    }

    private void EnsureAllowed()
    {
        if (checkSafety is not null) { checkSafety(); }
        else { NetworkSafetyGuard.EnsureAllowed("change the system proxy"); }
    }

    private sealed record ProxyJournal(DesktopProxyKind Kind, ProxyChange[] Changes);
    private sealed record ProxyChange(string Scope, string Key, string Previous, string Applied);
    private sealed record MacProxy(bool Enabled, string Host, int Port, bool Authenticated);
}
