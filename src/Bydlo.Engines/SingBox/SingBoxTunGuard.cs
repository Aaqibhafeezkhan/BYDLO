using System.Net;
using System.Net.Sockets;
using Bydlo.Platform;
using System.Text.Json;
using Bydlo.Core.Config;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Engines.SingBox;

/// <summary>Session-scoped strict TUN; no persistent firewall rules or services.</summary>
internal sealed class SingBoxTunGuard(
    IProcessRunner runner,
    LogRedactor redactor,
    EngineLocator locator,
    IPrivilegeService privileges,
    EngineKind? backendKind = null)
    : ProcessEngineBase(runner, redactor)
{
    public override EngineDescriptor Descriptor => SingBoxEngine.Info;
    protected override bool AliveMeansReady => false;
    public override bool CanHandle(Profile profile) => SingBoxConfigBuilder.Supported.Contains(profile.Protocol);
    public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) =>
        new SingBoxEngine(Runner, Redactor, locator, privileges).CheckAvailabilityAsync(settings, ct);

    protected override async Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct)
    {
        var config = new SingBoxConfigBuilder().BuildKillSwitch(request.Settings, request.Settings.LocalProxyPort, locator.Platform.Os, TransportExclusions(request));
        var path = Path.Combine(request.WorkDirectory, "tun.json");
        await SecureFile.WriteAsync(path, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
        return new ProcessSpec
        {
            FileName = availability.ExecutablePath!,
            Arguments = ["run", "-c", path, "-D", request.WorkDirectory, "--disable-color"],
            WorkingDirectory = request.WorkDirectory,
            DisplayName = RetainedTunEngine.GuardProcessName,
            GracefulTermination = true,
        };
    }

    /// <summary>
    /// Loop avoidance for a foreign transport (issue #212): the transport server address
    /// stays outside the TUN and the transport process itself is routed directly.
    /// Processes are full executable paths matched via sing-box process_path (issue #339):
    /// a bare process_name would also exempt any unrelated process with the same file name.
    /// </summary>
    internal KillSwitchExclusions? TransportExclusions(EngineStartRequest request)
    {
        if (backendKind is null || request.Profile is not { } profile)
        {
            return null;
        }
        var addresses = new List<string>();
        if (IPAddress.TryParse(profile.Host, out _))
        {
            addresses.Add(profile.Host);
        }
        var processes = new List<string>();
        if (locator.Locate(backendKind.Value, request.Settings) is { Length: > 0 } exe)
        {
            processes.Add(exe);
        }
        return addresses.Count == 0 && processes.Count == 0 ? null : new KillSwitchExclusions(addresses, processes);
    }

    protected override bool IsReadyLine(string line) => line.Contains("sing-box started", StringComparison.OrdinalIgnoreCase);
}
