using Bydlo.Platform;
using Bydlo.Core.Routing;
using Bydlo.Engines.Groups;
using Grpc.Net.Client;
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
    : ProcessEngineBase(runner, redactor), IServerGroupSource
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
    private GroupMonitor? _groupMonitor;
    private GrpcChannel? _groupChannel;
    private int _groupPort;
    public Guid? SelectedGroupProfileId => _groupMonitor?.Selected;

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

        if (locator.Platform.Os == OsFamily.MacOS && settings.Routing.OrderedRules.Any(rule => rule.Condition == RoutingCondition.Process))
        {
            return Task.FromResult(EngineAvailability.NotImplemented("Xray process rules require Windows or Linux.", "routing.rules.unsupported"));
        }

        if (settings.Routing.OrderedRules.Any(rule => RoutingPresets.UnsupportedXrayRule(settings.Routing, rule)))
        {
            return Task.FromResult(EngineAvailability.NotImplemented("An SRS rule set cannot be used by Xray.", "routing.rules.unsupported"));
        }

        try
        {
            XrayConfigBuilder.ValidateDnsSettings(settings.Routing);
        }
        catch (EngineException ex)
        {
            return Task.FromResult(EngineAvailability.NotImplemented(ex.Message, ex.LocalizationKey));
        }

        var exe = locator.Locate(EngineKind.Xray, settings);
        return Task.FromResult(exe is null
            ? new EngineAvailability(AvailabilityStatus.NotInstalled, "Xray-core is not installed. Install it from Tools → Engines.") { LocalizationKey = "error.engine.xray.missing" }
            : EngineAvailability.Ready(exe));
    }

    protected override async Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct)
    {
        var profile = request.Profile ?? throw new EngineException("No profile selected.");
        if (request.LocalProxyAuth is { } lan)
        {
            Redactor.RegisterSecret(lan.Password);
        }

        if (request.Settings.Routing.Preset != RoutingPreset.All)
        {
            throw new EngineException("routing.preset.singbox", localizationKey: "routing.preset.singbox");
        }
        var routing = request.Settings.Routing;
        new RoutingRuleBook(routing.OrderedRules, routing.RuleSetSources).Validate();
        var assets = RoutingPresets.XraySources(routing);
        foreach (var source in assets)
        {
            if (request.RuleSetDirectory is not { } directory)
            {
                throw new EngineException("routing.preset.missing", localizationKey: "routing.preset.missing");
            }
            var path = RoutingPresets.CachedPath(directory, source);
            try
            {
                var destination = Path.Combine(request.WorkDirectory, source.Tag);
                if (new FileInfo(path).Length > source.MaxBytes) { throw new IOException("Rule set exceeds its size limit."); }
                File.Copy(path, destination, overwrite: true);
                await IntegrityVerifier.VerifyFileAsync(destination, source.Sha256, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or IntegrityException)
            {
                throw new EngineException("routing.preset.missing", localizationKey: "routing.preset.missing");
            }
        }
        _groupPort = request.Group is null ? 0 : SingBoxEngine.FindFreeLoopbackPort();
        var ctx = new BuildContext(request.Settings, ClashApiPort: _groupPort == 0 ? null : _groupPort, LocalProxyAuth: request.LocalProxyAuth);
        var json = request.Group is { } group ? _builder.BuildGroup(group, request.GroupMembers ?? [], ctx).ToJsonString()
            : request.Chain is { } chain ? _builder.BuildChainJson(chain, ctx) : _builder.BuildJson(profile, request.Secrets, ctx);
        var configPath = Path.Combine(request.WorkDirectory, "xray.json");
        await SecureFile.WriteAsync(configPath, json, ct).ConfigureAwait(false);
        return new ProcessSpec
        {
            FileName = availability.ExecutablePath!,
            Arguments = ["run", "-c", configPath],
            WorkingDirectory = Path.GetDirectoryName(availability.ExecutablePath!),
            DisplayName = "xray",
            Environment = assets.Count == 0 ? new Dictionary<string, string>() :
                new Dictionary<string, string> { ["XRAY_LOCATION_ASSET"] = request.WorkDirectory },
        };
    }

    protected override bool IsReadyLine(string line) => line.Contains("started", StringComparison.OrdinalIgnoreCase) && line.Contains("Xray", StringComparison.OrdinalIgnoreCase);

    protected override Task OnStartedAsync(EngineStartRequest request, CancellationToken ct)
    {
        if (request.Group is { } group && request.GroupMembers is { } members)
        {
            _groupChannel = GrpcChannel.ForAddress($"http://127.0.0.1:{_groupPort}", new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false },
                MaxReceiveMessageSize = 1024 * 1024,
            });
            _groupMonitor = new GroupMonitor(group, members, new XrayGroupController(_groupChannel, members), false);
            _groupMonitor.Start();
        }
        return Task.CompletedTask;
    }

    protected override async Task OnStoppingAsync()
    {
        if (_groupMonitor is { } monitor) { await monitor.DisposeAsync().ConfigureAwait(false); _groupMonitor = null; }
        _groupChannel?.Dispose();
        _groupChannel = null;
    }

    protected override string? TryParseFatal(string line) =>
        line.Contains("Failed to start", StringComparison.OrdinalIgnoreCase) || line.Contains("failed to load config", StringComparison.OrdinalIgnoreCase) ? line : null;
}
