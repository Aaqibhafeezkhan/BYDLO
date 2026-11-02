using System.Net;
using System.Text.Json;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Engines.SingBox;
using Bydlo.Engines.WireGuard;

namespace Bydlo.Engines.Amnezia;

/// <summary>Upstream amneziawg-go in a supervised process; no official desktop client.</summary>
public sealed class AmneziaEngine(IProcessRunner runner, LogRedactor redactor, EngineLocator locator,
    IPrivilegeService privileges, IAmneziaNetwork network,
    Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) : ProcessEngineBase(runner, redactor)
{
    public static readonly EngineDescriptor Info = TunnelServiceEngine.AmneziaInfo with
    {
        License = "MIT (amneziawg-go); GPL-3.0+ (host); Wintun binary license (Windows)",
    };

    private AmneziaConfig? _config;
    private string? _configFile;
    public override EngineDescriptor Descriptor => Info;
    protected override bool AliveMeansReady => false;
    public override bool CanHandle(Profile profile) => profile.Protocol == ProtocolKind.AmneziaWG;
    protected override bool IsReadyLine(string line) => line == "BYDLO_AMNEZIA_READY";

    public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (Info.SupportOn(locator.Platform.Os) != SupportLevel.Implemented)
        { return Task.FromResult(EngineAvailability.NotImplemented("AmneziaWG is not implemented on this platform.", "error.engine.tunnel.platform")); }
        var exe = locator.Locate(EngineKind.AmneziaWG, settings);
        if (exe is null)
        { return Task.FromResult(new EngineAvailability(AvailabilityStatus.NotInstalled, "Bundled AmneziaWG is missing.") { LocalizationKey = "error.engine.amnezia.missing" }); }
        if (Path.GetFileNameWithoutExtension(exe).Equals("amneziawg", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(exe).Equals("awg-quick", StringComparison.OrdinalIgnoreCase))
        { return Task.FromResult(EngineAvailability.Unsupported("The custom path selects a legacy client; select the bundled BYDLO host.", "error.engine.amnezia.legacy")); }
        return Task.FromResult(privileges.IsElevated ? EngineAvailability.Ready(exe)
            : new EngineAvailability(AvailabilityStatus.NeedsElevation, "Creating a tunnel needs administrator rights.", exe) { LocalizationKey = "error.engine.tunnel.admin" });
    }

    protected override async Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct)
    {
        ChainGuards.ThrowIfChained(EngineKind.AmneziaWG, request);
        var raw = request.Secrets[Keys.RawConfig] ?? throw new EngineException("Profile has no stored configuration.");
        try { _config = await AmneziaConfig.ParseAsync(raw, resolve ?? ResolveAsync, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or ArgumentException)
        { throw new EngineException("Invalid or unsupported AmneziaWG configuration.", ex, "error.engine.amnezia.config"); }
        // The UAPI representation contains hex keys, unlike the original profile's base64 keys.
        foreach (var line in _config.Uapi.Split('\n'))
        {
            if (line.StartsWith("private_key=", StringComparison.Ordinal) || line.StartsWith("preshared_key=", StringComparison.Ordinal))
            { Redactor.RegisterSecret(line[(line.IndexOf('=') + 1)..].Trim()); }
        }
        await ConfigureNetworkAsync(() => network.PrepareAsync(_config.Endpoints, ct)).ConfigureAwait(false);
        _configFile = Path.Combine(request.WorkDirectory, "amnezia.json");
        await SecureFile.WriteAsync(_configFile, JsonSerializer.Serialize(new { Name = "bydlo-awg", MTU = _config.Mtu, UAPI = _config.Uapi }), ct).ConfigureAwait(false);
        return new ProcessSpec
        {
            FileName = availability.ExecutablePath!, Arguments = [_configFile, Environment.ProcessId.ToString()],
            WorkingDirectory = Path.GetDirectoryName(availability.ExecutablePath), DisplayName = "amneziawg", GracefulTermination = true,
        };
    }

    protected override Task OnStartedAsync(EngineStartRequest request, CancellationToken ct) =>
        ConfigureNetworkAsync(() => network.ApplyAsync(_config!.Addresses, _config.Dns, _config.Routes, _config.Mtu, ct));

    protected override async Task OnStoppingAsync()
    {
        await network.RemoveAsync().ConfigureAwait(false);
        if (_configFile is not null) { SecureFile.DeleteQuietly(_configFile); }
        _configFile = null;
        _config = null;
    }

    private static Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        NetworkSafetyGuard.EnsureAllowed("resolve an AmneziaWG endpoint");
        return Dns.GetHostAddressesAsync(host, ct);
    }

    private static async Task ConfigureNetworkAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        { throw new EngineException("AmneziaWG network setup failed; check privileges, system tools and the recovery journal.", ex, "error.engine.amnezia.network"); }
    }
}
