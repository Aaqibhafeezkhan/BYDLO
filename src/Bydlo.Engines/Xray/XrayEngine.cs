using Bydlo.Core.Config;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Engines.SingBox;

namespace Bydlo.Engines.Xray;

/// <summary>
/// Xray-core adapter (local proxy mode). Required for XHTTP transport; otherwise an alternative to sing-box.
/// Live traffic statistics are not wired up yet (Xray exposes them over gRPC only).
/// </summary>
public sealed class XrayEngine(IProcessRunner runner, LogRedactor redactor, EngineLocator locator)
    : ProcessEngineBase(runner, redactor)
{
    public static readonly EngineDescriptor Info = new()
    {
        Kind = EngineKind.Xray,
        DisplayName = "Xray-core",
        Upstream = "https://github.com/XTLS/Xray-core",
        License = "MPL-2.0",
        Protocols = XrayConfigBuilder.Supported,
        Platforms = new Dictionary<OsFamily, SupportLevel>
        {
            [OsFamily.Windows] = SupportLevel.Implemented,
            [OsFamily.Linux] = SupportLevel.Implemented,
            [OsFamily.MacOS] = SupportLevel.Implemented,
            [OsFamily.Android] = SupportLevel.Planned,
        },
    };

    private readonly XrayConfigBuilder _builder = new();

    public override EngineDescriptor Descriptor => Info;

    public override bool CanHandle(Profile profile) => XrayConfigBuilder.Supported.Contains(profile.Protocol);

    public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (Info.SupportOn(locator.Platform.Os) != SupportLevel.Implemented)
        {
            return Task.FromResult(EngineAvailability.NotImplemented("Xray on this platform is planned.", "error.engine.xray.platform"));
        }

        if (settings.Mode == ConnectionMode.Tun)
        {
            return Task.FromResult(EngineAvailability.NotImplemented("TUN mode with Xray is not implemented. Use Local proxy mode or the sing-box engine.", "error.engine.xray.tun"));
        }

        var exe = locator.Locate(EngineKind.Xray, settings);
        return Task.FromResult(exe is null
            ? new EngineAvailability(AvailabilityStatus.NotInstalled, "Xray-core is not installed. Install it from Tools → Engines.") { LocalizationKey = "error.engine.xray.missing" }
            : EngineAvailability.Ready(exe));
    }

    protected override async Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct)
    {
        var profile = request.Profile ?? throw new EngineException("No profile selected.");
        var json = _builder.BuildJson(profile, request.Secrets, new BuildContext(request.Settings));
        var configPath = Path.Combine(request.WorkDirectory, "xray.json");
        await SecureFile.WriteAsync(configPath, json, ct).ConfigureAwait(false);
        return new ProcessSpec
        {
            FileName = availability.ExecutablePath!,
            Arguments = ["run", "-c", configPath],
            WorkingDirectory = Path.GetDirectoryName(availability.ExecutablePath!),
            DisplayName = "xray",
        };
    }

    protected override bool IsReadyLine(string line) => line.Contains("started", StringComparison.OrdinalIgnoreCase) && line.Contains("Xray", StringComparison.OrdinalIgnoreCase);

    protected override string? TryParseFatal(string line) =>
        line.Contains("Failed to start", StringComparison.OrdinalIgnoreCase) || line.Contains("failed to load config", StringComparison.OrdinalIgnoreCase) ? line : null;
}
