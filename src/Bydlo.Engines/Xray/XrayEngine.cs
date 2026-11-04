using Bydlo.Platform;
using Bydlo.Core.Routing;
using Bydlo.Engines.Groups;
using Bydlo.Engines.Groups.Rpc;
using Grpc.Core;
using Grpc.Net.Client;
using Bydlo.Core.Config;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Engines.SingBox;

namespace Bydlo.Engines.Xray;

/// <summary>Live traffic statistics are polled from the local StatsService (issue #211).</summary>
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
    private GrpcChannel? _statsChannel;
    private CancellationTokenSource? _trafficCts;
    private int _statsPort;
    public Guid? SelectedGroupProfileId => _groupMonitor?.Selected;
    private string? _configPath;
    private readonly List<string> _copiedAssets = [];

    public override EngineDescriptor Descriptor => Info;

    public override bool CanHandle(Profile profile) => XrayConfigBuilder.Supported.Contains(profile.Protocol);

    public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (Info.SupportOn(locator.Platform.Os) != SupportLevel.Implemented)
        {
            return Task.FromResult(EngineAvailability.NotImplemented("Xray on this platform is planned.", "error.engine.xray.platform"));
        }

        if (settings.Mode == ConnectionMode.Tun && !settings.KillSwitch)
        {
            return Task.FromResult(EngineAvailability.NotImplemented("TUN mode with Xray is not implemented. Use Local proxy mode, the sing-box engine, or TUN with kill switch.", "error.engine.xray.tun"));
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
        _copiedAssets.Clear();
        _configPath = null;
        foreach (var source in assets)
        {
            if (request.RuleSetDirectory is not { } directory)
            {
                throw new EngineException("routing.preset.missing", localizationKey: "routing.preset.missing");
            }
            var path = RoutingPresets.CachedPath(directory, source);
            var destination = Path.Combine(request.WorkDirectory, source.Tag);
            try
            {
                if (new FileInfo(path).Length > source.MaxBytes) { throw new IOException("Rule set exceeds its size limit."); }
                File.Copy(path, destination, overwrite: true);
                _copiedAssets.Add(destination);
                await IntegrityVerifier.VerifyFileAsync(destination, source.Sha256, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or IntegrityException)
            {
                SecureFile.DeleteQuietly(destination);
                _copiedAssets.Remove(destination);
                throw new EngineException("routing.preset.missing", localizationKey: "routing.preset.missing");
            }
        }
        _statsPort = SingBoxEngine.FindFreeLoopbackPort();
        var ctx = new BuildContext(request.Settings, ClashApiPort: _statsPort, LocalProxyAuth: request.LocalProxyAuth);
        var json = request.Group is { } group ? _builder.BuildGroup(group, request.GroupMembers ?? [], ctx).ToJsonString()
            : request.Chain is { } chain ? _builder.BuildChainJson(chain, ctx) : _builder.BuildJson(profile, request.Secrets, ctx);
        _configPath = Path.Combine(request.WorkDirectory, "xray.json");
        await SecureFile.WriteAsync(_configPath, json, ct).ConfigureAwait(false);
        return new ProcessSpec
        {
            FileName = availability.ExecutablePath!,
            Arguments = ["run", "-c", _configPath],
            WorkingDirectory = Path.GetDirectoryName(availability.ExecutablePath!),
            DisplayName = "xray",
            Environment = assets.Count == 0 ? new Dictionary<string, string>() :
                new Dictionary<string, string> { ["XRAY_LOCATION_ASSET"] = request.WorkDirectory },
        };
    }

    protected override async Task OnStoppingAsync()
    {
        if (_groupMonitor is { } monitor) { await monitor.DisposeAsync().ConfigureAwait(false); _groupMonitor = null; }
        _trafficCts?.Cancel();
        _trafficCts = null;
        _statsChannel?.Dispose();
        _statsChannel = null;
        if (_configPath is not null)
        {
            SecureFile.DeleteQuietly(_configPath);
            _configPath = null;
        }
        foreach (var asset in _copiedAssets)
        {
            SecureFile.DeleteQuietly(asset);
        }
        _copiedAssets.Clear();
    }

    protected override bool IsReadyLine(string line) => line.Contains("started", StringComparison.OrdinalIgnoreCase) && line.Contains("Xray", StringComparison.OrdinalIgnoreCase);

    protected override Task OnStartedAsync(EngineStartRequest request, CancellationToken ct)
    {
        // StatsService shares the loopback gRPC channel (see #211); it never goes through a proxy.
        _statsChannel = GrpcChannel.ForAddress($"http://127.0.0.1:{_statsPort}", new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false },
            MaxReceiveMessageSize = 1024 * 1024,
        });
        if (request.Group is { } group && request.GroupMembers is { } members)
        {
            _groupMonitor = new GroupMonitor(group, members, new XrayGroupController(_statsChannel, members), false);
            _groupMonitor.Start();
        }
        _trafficCts = new CancellationTokenSource();
        _ = ReadTrafficAsync(request, _trafficCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>Polls StatsService deltas once a second and emits them as traffic samples.</summary>
    private async Task ReadTrafficAsync(EngineStartRequest request, CancellationToken ct)
    {
        // Tunnel outbounds per request shape (issue #211): the single proxy tag, the exit
        // hop of a chain (mirrors XrayConfigBuilder.Tag), member tags of a group, or — for
        // custom JSON with unknown tags — every outbound except direct/block.
        IReadOnlyCollection<string>? tags = request switch
        {
            { Group: not null, GroupMembers: { } members } => members.Select(m => ServerGroup.Tag(m.Profile.Id)).ToList(),
            { Chain: { } chain } => [$"proxy-{chain.Count - 1}"],
            { Profile: { Protocol: ProtocolKind.XrayConfig } } => null,
            _ => ["proxy"],
        };
        var reader = new XrayTrafficReader(new StatsService.StatsServiceClient(_statsChannel!), tags);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await reader.ReadAsync(ct).ConfigureAwait(false) is { } sample)
                {
                    EmitTraffic(sample);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is RpcException or InvalidOperationException)
            {
                // A failed poll (controller restarting) must not fault this fire-and-forget loop.
                if (!await PauseAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false))
                {
                    return;
                }
                continue;
            }
            if (!await PauseAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>Reconnect pause that stays silent when the engine stops. Returns false when cancelled.</summary>
    private static async Task<bool> PauseAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    protected override string? TryParseFatal(string line) =>
        line.Contains("Failed to start", StringComparison.OrdinalIgnoreCase) || line.Contains("failed to load config", StringComparison.OrdinalIgnoreCase) ? line : null;
}
