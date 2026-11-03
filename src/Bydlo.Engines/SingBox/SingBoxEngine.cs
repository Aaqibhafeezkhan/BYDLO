using Bydlo.Platform;
using Bydlo.Core.Routing;
using Bydlo.Engines.Groups;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Bydlo.Core.Config;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Engines.SingBox;

/// <summary>sing-box adapter: VLESS/REALITY, VMess, Trojan, Shadowsocks, Hysteria 2, TUIC, SOCKS/HTTP, WireGuard.</summary>
public sealed class SingBoxEngine(IProcessRunner runner, LogRedactor redactor, EngineLocator locator, IPrivilegeService privileges)
    : ProcessEngineBase(runner, redactor), IActiveConnectionSource, IServerGroupSource
{
    public static readonly EngineDescriptor Info = new()
    {
        Kind = EngineKind.SingBox,
        DisplayName = "sing-box",
        Upstream = "https://github.com/SagerNet/sing-box",
        License = "GPL-3.0-or-later",
        Protocols = SingBoxConfigBuilder.Supported,
        Platforms = new Dictionary<OsFamily, SupportLevel>
        {
            [OsFamily.Windows] = SupportLevel.Implemented,
            [OsFamily.Linux] = SupportLevel.Implemented,
            [OsFamily.MacOS] = SupportLevel.Implemented,
            [OsFamily.Android] = SupportLevel.Planned,
        },
    };

    private static readonly HttpClient ClashHttp = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    public Task<IReadOnlyList<ActiveConnection>> ListConnectionsAsync(CancellationToken ct = default) =>
        new ClashConnectionsClient(ClashHttp, _apiPort, _apiSecret).ListConnectionsAsync(ct);

    public Task CloseConnectionAsync(string id, CancellationToken ct = default) =>
        new ClashConnectionsClient(ClashHttp, _apiPort, _apiSecret).CloseConnectionAsync(id, ct);

    private readonly SingBoxConfigBuilder _builder = new();
    private GroupMonitor? _groupMonitor;
    public Guid? SelectedGroupProfileId => _groupMonitor?.Selected;
    private CancellationTokenSource? _trafficCts;
    private int _apiPort;
    private string _apiSecret = "";
    private string? _configPath;

    public override EngineDescriptor Descriptor => Info;

    public override bool CanHandle(Profile profile) => SingBoxConfigBuilder.Supported.Contains(profile.Protocol);

    public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (Info.SupportOn(locator.Platform.Os) != SupportLevel.Implemented)
        {
            return Task.FromResult(EngineAvailability.NotImplemented("sing-box on this platform is planned (VpnService integration).", "error.engine.singbox.platform"));
        }

        if (settings.Mode != ConnectionMode.Tun && settings.Routing.OrderedRules.Any(rule => rule.Condition == RoutingCondition.Process))
        {
            return Task.FromResult(EngineAvailability.NotImplemented("Process rules require TUN mode in sing-box.", "routing.rules.unsupported"));
        }

        if (settings.Routing.OrderedRules.Any(rule => rule.Condition is RoutingCondition.GeoSite or RoutingCondition.GeoIp &&
            (RoutingPresets.Find(settings.Routing, RoutingPresets.SingBoxTag(rule.Condition, rule.Value.Trim())) is not { } source || RoutingPresets.IsXraySource(source))))
        {
            return Task.FromResult(EngineAvailability.NotImplemented("No sing-box source is registered for this geo condition.", "routing.rules.unsupported"));
        }

        var exe = locator.Locate(EngineKind.SingBox, settings);
        if (exe is null)
        {
            return Task.FromResult(new EngineAvailability(AvailabilityStatus.NotInstalled, "sing-box is not installed. Install it from Tools → Engines.") { LocalizationKey = "error.engine.singbox.missing" });
        }

        if (settings.Mode == ConnectionMode.Tun && !privileges.IsElevated)
        {
            return Task.FromResult(new EngineAvailability(AvailabilityStatus.NeedsElevation, "TUN mode needs administrator rights. Restart BYDLO as administrator or use Local proxy mode.", exe) { LocalizationKey = "error.engine.tun.admin" });
        }

        return Task.FromResult(EngineAvailability.Ready(exe));
    }

    protected override async Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct)
    {
        var profile = request.Profile ?? throw new EngineException("No profile selected.");
        _apiPort = FindFreeLoopbackPort();
        _apiSecret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        Redactor.RegisterSecret(_apiSecret);
        if (request.LocalProxyAuth is { } lan)
        {
            Redactor.RegisterSecret(lan.Password);
        }

        var routing = request.Settings.Routing;
        new RoutingRuleBook(routing.OrderedRules, routing.RuleSetSources).Validate();
        var ruleSets = new Dictionary<string, string>(StringComparer.Ordinal);
        var sources = RoutingPresets.Sources(routing.Preset, routing.PresetCountry).ToDictionary(source => source.Tag);
        var referencedSets = routing.OrderedRules.Where(rule => rule.Condition is RoutingCondition.GeoSite or RoutingCondition.GeoIp)
            .Select(rule => RoutingPresets.SingBoxTag(rule.Condition, rule.Value.Trim())).Concat(routing.DirectDnsRuleSets).Concat(routing.RemoteDnsRuleSets);
        foreach (var tag in referencedSets)
        {
            if (RoutingPresets.Find(routing, tag.Trim()) is { } source && !RoutingPresets.IsXraySource(source))
            {
                sources.TryAdd(source.Tag, source);
            }
        }
        foreach (var source in sources.Values)
        {
            if (request.RuleSetDirectory is not { } directory)
            {
                throw new EngineException("routing.preset.missing", localizationKey: "routing.preset.missing");
            }
            var path = RoutingPresets.CachedPath(directory, source);
            try
            {
                if (new FileInfo(path).Length > source.MaxBytes) { throw new IOException("Rule set exceeds its size limit."); }
                await IntegrityVerifier.VerifyFileAsync(path, source.Sha256, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or IntegrityException)
            {
                throw new EngineException("routing.preset.missing", localizationKey: "routing.preset.missing");
            }
            ruleSets.Add(source.Tag, path);
        }
        var ctx = new BuildContext(request.Settings, _apiPort, _apiSecret, request.LocalProxyAuth, ruleSets, locator.Platform.Os);
        var json = request.Group is { } group ? _builder.BuildGroup(group, request.GroupMembers ?? [], ctx).ToJsonString()
            : request.Chain is { } chain ? _builder.BuildChainJson(chain, ctx) : _builder.BuildJson(profile, request.Secrets, ctx);
        _configPath = Path.Combine(request.WorkDirectory, "sing-box.json");
        await SecureFile.WriteAsync(_configPath, json, ct).ConfigureAwait(false);

        return new ProcessSpec
        {
            FileName = availability.ExecutablePath!,
            Arguments = ["run", "-c", _configPath, "-D", request.WorkDirectory, "--disable-color"],
            WorkingDirectory = request.WorkDirectory,
            DisplayName = "sing-box",
        };
    }

    protected override bool IsReadyLine(string line) => line.Contains("sing-box started", StringComparison.OrdinalIgnoreCase);

    protected override string? TryParseFatal(string line) =>
        line.Contains("FATAL", StringComparison.Ordinal) || line.Contains("start service:", StringComparison.OrdinalIgnoreCase) ? line : null;

    protected override Task OnStartedAsync(EngineStartRequest request, CancellationToken ct)
    {
        if (request.Group is { } group && request.GroupMembers is { } members)
        {
            _groupMonitor = new GroupMonitor(group, members, new ClashGroupController(ClashHttp, _apiPort, _apiSecret, members), true);
            _groupMonitor.Start();
        }
        _trafficCts = new CancellationTokenSource();
        _ = ReadTrafficAsync(_trafficCts.Token);
        return Task.CompletedTask;
    }

    protected override async Task OnStoppingAsync()
    {
        if (_groupMonitor is { } monitor) { await monitor.DisposeAsync().ConfigureAwait(false); _groupMonitor = null; }
        _trafficCts?.Cancel();
        _trafficCts = null;
        if (_configPath is not null)
        {
            SecureFile.DeleteQuietly(_configPath);
            _configPath = null;
        }
    }

    /// <summary>Reads the Clash API <c>/traffic</c> stream (one JSON line per second) from the local controller.</summary>
    private async Task ReadTrafficAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        long totalUp = 0, totalDown = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_apiPort}/traffic");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiSecret);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    using var doc = JsonDocument.Parse(line);
                    var up = doc.RootElement.GetProperty("up").GetInt64();
                    var down = doc.RootElement.GetProperty("down").GetInt64();
                    totalUp += up;
                    totalDown += down;
                    EmitTraffic(new TrafficSample(DateTimeOffset.Now, up, down, totalUp, totalDown));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or KeyNotFoundException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
    }

    internal static int FindFreeLoopbackPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
