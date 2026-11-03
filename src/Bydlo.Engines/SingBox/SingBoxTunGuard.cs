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
internal sealed class SingBoxTunGuard(IProcessRunner runner, LogRedactor redactor, EngineLocator locator, IPrivilegeService privileges)
    : ProcessEngineBase(runner, redactor)
{
    public override EngineDescriptor Descriptor => SingBoxEngine.Info;
    protected override bool AliveMeansReady => false;
    public override bool CanHandle(Profile profile) => SingBoxConfigBuilder.Supported.Contains(profile.Protocol);
    public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) =>
        new SingBoxEngine(Runner, Redactor, locator, privileges).CheckAvailabilityAsync(settings, ct);

    protected override async Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct)
    {
        var config = new SingBoxConfigBuilder().BuildKillSwitch(request.Settings, request.Settings.LocalProxyPort, locator.Platform.Os);
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

    protected override bool IsReadyLine(string line) => line.Contains("sing-box started", StringComparison.OrdinalIgnoreCase);
}
