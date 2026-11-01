using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Engines.SingBox;

namespace Bydlo.Engines.WireGuard;

/// <summary>
/// Native WireGuard / AmneziaWG through the official clients:
/// Windows — <c>wireguard.exe /installtunnelservice</c> (or <c>amneziawg.exe</c>);
/// Linux/macOS — <c>wg-quick up</c> (or <c>awg-quick</c>). These are privileged system changes that exist only while connected.
/// </summary>
public sealed class TunnelServiceEngine : IEngine
{
    public const string InterfaceName = "bydlo-wg";

    public static readonly EngineDescriptor WireGuardInfo = new()
    {
        Kind = EngineKind.WireGuard,
        DisplayName = "WireGuard (native)",
        Upstream = "https://www.wireguard.com",
        License = "MIT (wireguard-windows), GPL-2.0 (wg-quick)",
        Protocols = new HashSet<ProtocolKind> { ProtocolKind.WireGuard },
        Platforms = new Dictionary<OsFamily, SupportLevel>
        {
            [OsFamily.Windows] = SupportLevel.Implemented,
            [OsFamily.Linux] = SupportLevel.Implemented,
            [OsFamily.MacOS] = SupportLevel.Implemented,
            [OsFamily.Android] = SupportLevel.Planned,
        },
    };

    public static readonly EngineDescriptor AmneziaInfo = new()
    {
        Kind = EngineKind.AmneziaWG,
        DisplayName = "AmneziaWG",
        Upstream = "https://github.com/amnezia-vpn",
        License = "MIT (amneziawg-windows, amneziawg-go), GPL-2.0 (awg-quick)",
        Protocols = new HashSet<ProtocolKind> { ProtocolKind.AmneziaWG },
        Platforms = new Dictionary<OsFamily, SupportLevel>
        {
            [OsFamily.Windows] = SupportLevel.Implemented,
            [OsFamily.Linux] = SupportLevel.Implemented,
            [OsFamily.MacOS] = SupportLevel.Planned,
            [OsFamily.Android] = SupportLevel.Planned,
        },
    };

    private readonly IProcessRunner _runner;
    private readonly LogRedactor _redactor;
    private readonly EngineLocator _locator;
    private readonly IPrivilegeService _privileges;
    private string? _exe;
    private string? _configPath;

    public TunnelServiceEngine(bool amnezia, IProcessRunner runner, LogRedactor redactor, EngineLocator locator, IPrivilegeService privileges)
    {
        Descriptor = amnezia ? AmneziaInfo : WireGuardInfo;
        _runner = runner;
        _redactor = redactor;
        _locator = locator;
        _privileges = privileges;
    }

    public EngineDescriptor Descriptor { get; }

    public EngineState State { get; private set; } = EngineState.Stopped;

    public event EventHandler<EngineStateChangedEventArgs>? StateChanged;

    public event EventHandler<string>? Log;

#pragma warning disable CS0067 // Native clients do not expose per-second counters to BYDLO yet.
    public event EventHandler<TrafficSample>? Traffic;
#pragma warning restore CS0067

    private bool IsWindows => _locator.Platform.Os == OsFamily.Windows;

    public bool CanHandle(Profile profile) => Descriptor.Protocols.Contains(profile.Protocol);

    public Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (Descriptor.SupportOn(_locator.Platform.Os) != SupportLevel.Implemented)
        {
            return Task.FromResult(EngineAvailability.NotImplemented($"{Descriptor.DisplayName} on this platform is planned.", "error.engine.tunnel.platform"));
        }

        var exe = _locator.Locate(Descriptor.Kind, settings);
        if (exe is null)
        {
            var hint = Descriptor.Kind == EngineKind.AmneziaWG
                ? "Install AmneziaWG (amneziawg-windows / amneziawg-tools)."
                : "Install WireGuard (wireguard.com/install) or use the sing-box engine.";
            return Task.FromResult(new EngineAvailability(AvailabilityStatus.NotInstalled, hint) { LocalizationKey = Descriptor.Kind == EngineKind.AmneziaWG ? "error.engine.amnezia.missing" : "error.engine.wireguard.missing" });
        }

        return Task.FromResult(_privileges.IsElevated
            ? EngineAvailability.Ready(exe)
            : new EngineAvailability(AvailabilityStatus.NeedsElevation, "Creating a tunnel needs administrator rights.", exe) { LocalizationKey = "error.engine.tunnel.admin" });
    }

    public async Task StartAsync(EngineStartRequest request, CancellationToken ct = default)
    {
        Set(EngineState.Starting);
        try
        {
            var availability = await CheckAvailabilityAsync(request.Settings, ct).ConfigureAwait(false);
            if (!availability.IsReady)
            {
                throw new EngineException(availability.Message, localizationKey: availability.LocalizationKey);
            }

            var raw = request.Secrets[Keys.RawConfig] ?? throw new EngineException("Profile has no stored configuration.");
            foreach (var secret in request.Secrets.Values.Values)
            {
                _redactor.RegisterSecret(secret);
            }

            _exe = availability.ExecutablePath!;
            _configPath = Path.Combine(request.WorkDirectory, InterfaceName + ".conf");
            await SecureFile.WriteAsync(_configPath, raw, ct).ConfigureAwait(false);

            var (code, output) = await _runner.RunToCompletionAsync(new ProcessSpec
            {
                FileName = _exe,
                Arguments = IsWindows ? ["/installtunnelservice", _configPath] : ["up", _configPath],
                DisplayName = Descriptor.DisplayName,
            }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            EmitOutput(output);
            if (code != 0)
            {
                throw new EngineException($"{Descriptor.DisplayName} failed to bring the tunnel up (code {code}).");
            }

            Set(EngineState.Running);
        }
        catch (Exception ex)
        {
            Cleanup();
            Set(EngineState.Failed, ex.Message);
            throw ex as EngineException ?? new EngineException(ex.Message, ex);
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_exe is null || _configPath is null)
        {
            Set(EngineState.Stopped);
            return;
        }

        Set(EngineState.Stopping);
        try
        {
            var (_, output) = await _runner.RunToCompletionAsync(new ProcessSpec
            {
                FileName = _exe,
                Arguments = IsWindows ? ["/uninstalltunnelservice", InterfaceName] : ["down", _configPath],
                DisplayName = Descriptor.DisplayName,
            }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            EmitOutput(output);
        }
        finally
        {
            Cleanup();
            Set(EngineState.Stopped);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (State == EngineState.Running)
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    private void Cleanup()
    {
        if (_configPath is not null)
        {
            SecureFile.DeleteQuietly(_configPath);
        }

        _configPath = null;
        _exe = null;
    }

    private void EmitOutput(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Log?.Invoke(this, _redactor.Redact(line));
        }
    }

    private void Set(EngineState state, string? error = null)
    {
        State = state;
        StateChanged?.Invoke(this, new EngineStateChangedEventArgs(state, error));
    }
}
