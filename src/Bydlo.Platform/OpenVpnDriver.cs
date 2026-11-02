using System.Net.NetworkInformation;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Core.Engines;

namespace Bydlo.Platform;

/// <summary>Explicit one-time setup, never invoked by engine startup or availability checks.</summary>
public sealed class OpenVpnDriver(IProcessRunner runner, IPrivilegeService privileges, Func<string?> packageDirectory,
    Func<bool>? installed = null, int? windowsBuild = null) : IOpenVpnDriver
{
    public const string AdapterName = "BYDLO-OpenVPN";
    public bool IsInstalled => installed?.Invoke() ?? (OperatingSystem.IsWindows() && NetworkInterface.GetAllNetworkInterfaces()
        .Any(n => n.Name == AdapterName && (n.Description.Contains("ovpn-dco", StringComparison.OrdinalIgnoreCase)
            || n.Description.Contains("OpenVPN Data Channel Offload", StringComparison.OrdinalIgnoreCase))));

    public async Task InstallAsync(bool userConsent, CancellationToken ct = default)
    {
        if (!userConsent) { throw new EngineException("Explicit consent is required to install the OpenVPN DCO driver.", localizationKey: "error.engine.openvpn.consent"); }
        NetworkSafetyGuard.EnsureAllowed("install the OpenVPN DCO driver");
        if (!privileges.IsElevated) { throw new EngineException("Restart BYDLO as administrator before installing DCO.", localizationKey: "error.engine.openvpn.driver.admin"); }
        if (IsInstalled) { return; }
        var root = packageDirectory() ?? throw new EngineException("Bundled OpenVPN package is missing.", localizationKey: "error.engine.openvpn.driver.package");
        var inf = Path.Combine(root, "dco", (windowsBuild ?? Environment.OSVersion.Version.Build) >= 22000 ? "Win11" : "Win10", "ovpn-dco.inf");
        var tapctl = Path.Combine(root, "tapctl.exe");
        if (!File.Exists(inf) || !File.Exists(tapctl)) { throw new EngineException("Bundled DCO package is incomplete.", localizationKey: "error.engine.openvpn.driver.package"); }
        // Windows verifies the signed catalog. No GUI, MSI installation, OpenVPN service or auto-reboot.
        var (code, _) = await runner.RunToCompletionAsync(new ProcessSpec
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe"),
            Arguments = ["/add-driver", inf, "/install"], DisplayName = "openvpn-dco-setup",
        }, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (code == 3010) { throw new EngineException("DCO was staged. Restart Windows, then retry setup in BYDLO.", localizationKey: "error.engine.openvpn.driver.reboot"); }
        if (code != 0) { throw new EngineException("Windows refused the DCO driver package.", localizationKey: "error.engine.openvpn.driver.refused"); }
        var (adapterCode, _) = await runner.RunToCompletionAsync(new ProcessSpec
        {
            FileName = tapctl, Arguments = ["create", "--hwid", "ovpn-dco", "--name", AdapterName],
            WorkingDirectory = root, DisplayName = "openvpn-dco-setup",
        }, TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        if (adapterCode != 0) { throw new EngineException("DCO was staged, but the BYDLO adapter could not be created. Retry setup.", localizationKey: "error.engine.openvpn.driver.adapter"); }
    }
}
