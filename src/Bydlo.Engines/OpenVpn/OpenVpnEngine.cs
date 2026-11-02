using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Engines.SingBox;

namespace Bydlo.Engines.OpenVpn;

/// <summary>OpenVPN community client adapter (<c>openvpn --config</c>). Needs administrator rights for routes / adapters.</summary>
public sealed class OpenVpnEngine(IProcessRunner runner, LogRedactor redactor, EngineLocator locator, IPrivilegeService privileges, IOpenVpnDriver? driver = null)
    : ProcessEngineBase(runner, redactor)
{
    public static readonly EngineDescriptor Info = new()
    {
        Kind = EngineKind.OpenVpn,
        DisplayName = "OpenVPN",
        Upstream = "https://github.com/OpenVPN/openvpn",
        License = "GPL-2.0 (with OpenSSL linking exception)",
        Protocols = new HashSet<ProtocolKind> { ProtocolKind.OpenVpn },
        Platforms = new Dictionary<OsFamily, SupportLevel>
        {
            [OsFamily.Windows] = SupportLevel.Implemented,
            [OsFamily.Linux] = SupportLevel.Implemented,
            [OsFamily.MacOS] = SupportLevel.Implemented,
            [OsFamily.Android] = SupportLevel.Planned,
        },
    };

    private string? _authFile;
    private string? _configFile;
    private EventWaitHandle? _exitEvent;

    public override EngineDescriptor Descriptor => Info;

    protected override TimeSpan ReadyTimeout => TimeSpan.FromSeconds(45);

    protected override bool AliveMeansReady => false;

    public override bool CanHandle(Profile profile) => profile.Protocol == ProtocolKind.OpenVpn;

    public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (Info.SupportOn(locator.Platform.Os) != SupportLevel.Implemented)
        {
            return Task.FromResult(EngineAvailability.NotImplemented("OpenVPN on this platform is planned.", "error.engine.openvpn.platform"));
        }

        var exe = locator.Locate(EngineKind.OpenVpn, settings);
        if (exe is null)
        {
            return Task.FromResult(new EngineAvailability(AvailabilityStatus.NotInstalled, "OpenVPN is not installed (openvpn.net/community-downloads).") { LocalizationKey = "error.engine.openvpn.missing" });
        }

        if (locator.Platform.Os == OsFamily.Windows && driver?.IsInstalled != true)
        {
            return Task.FromResult(new EngineAvailability(AvailabilityStatus.NotInstalled, "Set up the bundled OpenVPN DCO driver in Tools → Engines.", exe) { LocalizationKey = "error.engine.openvpn.driver" });
        }

        return Task.FromResult(privileges.IsElevated
            ? EngineAvailability.Ready(exe)
            : new EngineAvailability(AvailabilityStatus.NeedsElevation, "OpenVPN needs administrator rights.", exe) { LocalizationKey = "error.engine.openvpn.admin" });
    }

    protected override async Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct)
    {
        var raw = request.Secrets[Keys.RawConfig] ?? throw new EngineException("Profile has no stored .ovpn configuration.");
        _configFile = Path.Combine(request.WorkDirectory, "profile.ovpn");
        await SecureFile.WriteAsync(_configFile, raw, ct).ConfigureAwait(false);
        var args = new List<string> { "--config", _configFile, "--verb", "3", "--suppress-timestamps" };
        if (locator.Platform.Os == OsFamily.Windows)
        {
            args.AddRange(["--windows-driver", "ovpn-dco", "--dev-node", "BYDLO-OpenVPN"]);
        }
        else
        {
            // No host-specific DCO module is required by the portable build.
            args.Add("--disable-dco");
        }

        if (request.Profile?.Option("authUserPass") == "true")
        {
            var user = request.Secrets[Keys.Username];
            var pass = request.Secrets[Keys.Password];
            if (user is null || pass is null)
            {
                throw new EngineException("This OpenVPN profile needs a username and password. Edit the profile to add them.");
            }

            _authFile = Path.Combine(request.WorkDirectory, "auth.txt");
            await SecureFile.WriteAsync(_authFile, user + "\n" + pass + "\n", ct).ConfigureAwait(false);
            args.AddRange(["--auth-user-pass", _authFile]);
        }

        string? exitName = null;
        if (OperatingSystem.IsWindows() && locator.Platform.Os == OsFamily.Windows)
        {
            exitName = "Local\\BYDLO-OpenVPN-" + Guid.NewGuid().ToString("N");
            _exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, exitName);
            args.AddRange(["--service", exitName, "0"]);
        }
        return new ProcessSpec { FileName = availability.ExecutablePath!, Arguments = args, WorkingDirectory = request.WorkDirectory, DisplayName = "openvpn", GracefulTermination = true, WindowsExitEventName = exitName };
    }

    protected override bool IsReadyLine(string line) => line.Contains("Initialization Sequence Completed", StringComparison.Ordinal);

    protected override string? TryParseFatal(string line) =>
        line.Contains("AUTH_FAILED", StringComparison.Ordinal) ? "Authentication failed (wrong username or password)."
        : line.Contains("Exiting due to fatal error", StringComparison.Ordinal) || line.Contains("Options error", StringComparison.Ordinal) ? line
        : null;

    protected override Task OnStoppingAsync()
    {
        // The running child owns its own event handle; signal before closing the parent handle.
        var exitEvent = Interlocked.Exchange(ref _exitEvent, null);
        if (exitEvent is not null) { exitEvent.Set(); exitEvent.Dispose(); }
        if (_authFile is not null)
        {
            SecureFile.DeleteQuietly(_authFile);
        }

        if (_configFile is not null)
        {
            SecureFile.DeleteQuietly(_configFile);
        }

        return Task.CompletedTask;
    }
}
