using System.Net;
using System.Text.Json.Nodes;
using Bydlo.Core.Config;
using Bydlo.Core.Engines;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Engines.Groups.Rpc;
using Bydlo.Engines.Xray;
using Google.Protobuf;
using Grpc.Core;

namespace Bydlo.Tests;

/// <summary>Issue #211: live traffic statistics for Xray via gRPC StatsService.</summary>
public sealed class XrayTrafficTests
{
    private static QueryStatsResponse Response(params (string Name, long Value)[] stats)
    {
        var response = new QueryStatsResponse();
        foreach (var (name, value) in stats)
        {
            response.Stat.Add(new Stat { Name = name, Value = value });
        }
        return response;
    }

    [Fact]
    public void Stats_sum_uplink_and_downlink_for_the_tunnel_outbound()
    {
        var response = Response(
            ("outbound>>>proxy>>>traffic>>>uplink", 10),
            ("outbound>>>proxy>>>traffic>>>downlink", 20),
            ("outbound>>>direct>>>traffic>>>uplink", 1000),
            ("inbound>>>socks-in>>>traffic>>>downlink", 5),
            ("outbound>>>proxy>>>traffic>>>bogus", 7));
        Assert.True(XrayTrafficReader.TrySumTraffic(response, ["proxy"], out var up, out var down));
        Assert.Equal(10, up);
        Assert.Equal(20, down);
    }

    [Fact]
    public void Stats_without_tunnel_counters_report_nothing()
    {
        Assert.False(XrayTrafficReader.TrySumTraffic(Response(("outbound>>>direct>>>traffic>>>uplink", 1)), ["proxy"], out _, out _));
        Assert.False(XrayTrafficReader.TrySumTraffic(new QueryStatsResponse(), ["proxy"], out var up, out var down));
        Assert.Equal(0, up);
        Assert.Equal(0, down);
    }

    [Fact]
    public void Stats_sum_group_members_and_chain_exit_but_not_direct_or_block()
    {
        var response = Response(
            ("outbound>>>member-aaa>>>traffic>>>uplink", 3),
            ("outbound>>>member-bbb>>>traffic>>>downlink", 4),
            ("outbound>>>proxy>>>traffic>>>uplink", 100),
            ("outbound>>>direct>>>traffic>>>uplink", 1000),
            ("outbound>>>block>>>traffic>>>downlink", 1000));
        Assert.True(XrayTrafficReader.TrySumTraffic(response, ["member-aaa", "member-bbb"], out var up, out var down));
        Assert.Equal(3, up);
        Assert.Equal(4, down);
        Assert.True(XrayTrafficReader.TrySumTraffic(response, null, out var allUp, out var allDown));
        Assert.Equal(103, allUp);
        Assert.Equal(4, allDown);
    }

    [Fact]
    public void Chain_exit_tag_matches_the_builder()
    {
        var tag = XrayConfigBuilder.Tag(2);
        Assert.Equal("proxy-2", tag);
        var response = Response(
            ("outbound>>>proxy-0>>>traffic>>>uplink", 11),
            ("outbound>>>proxy-1>>>traffic>>>uplink", 12),
            ("outbound>>>proxy-2>>>traffic>>>uplink", 13));
        Assert.True(XrayTrafficReader.TrySumTraffic(response, [tag], out var up, out _));
        Assert.Equal(13, up);
    }

    [Fact]
    public void Group_build_keeps_stats_service()
    {
        var a = new ChainHop(new Profile { Name = "a", Host = "a.invalid", Port = 443, Protocol = ProtocolKind.Trojan },
            new ProfileSecrets { Values = new() { [Keys.Password] = "fake-a" } });
        var group = new ServerGroup { Name = "Test", Engine = EngineKind.Xray, ProfileIds = [a.Profile.Id] };
        var root = new XrayConfigBuilder().BuildGroup(group, [a], new(new AppSettings(), 1234));
        var services = root["api"]!["services"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Contains("StatsService", services);
        Assert.Contains("RoutingService", services);
        Assert.Contains("ObservatoryService", services);
    }

    private sealed class FakeStatsInvoker(QueryStatsResponse response) : CallInvoker
    {
        public List<string> Paths { get; } = [];

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            Paths.Add(method.FullName);
            return new AsyncUnaryCall<TResponse>(
                Task.FromResult((TResponse)(object)response),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            (TResponse)(object)response;

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }

    private static StatsService.StatsServiceClient Client(FakeStatsInvoker invoker) => new(invoker);

    [Fact]
    public async Task Reader_emits_deltas_and_accumulates_totals()
    {
        var invoker = new FakeStatsInvoker(Response(
            ("outbound>>>proxy>>>traffic>>>uplink", 10),
            ("outbound>>>proxy>>>traffic>>>downlink", 20)));
        var reader = new XrayTrafficReader(Client(invoker), ["proxy"]);
        var first = await reader.ReadAsync();
        Assert.NotNull(first);
        Assert.Equal(10, first.UploadBytesPerSecond);
        Assert.Equal(20, first.DownloadBytesPerSecond);
        Assert.Equal(10, first.TotalUpload);
        Assert.Equal(20, first.TotalDownload);

        // The fake service replays the same per-poll deltas; the reader accumulates them.
        var second = await reader.ReadAsync();
        Assert.NotNull(second);
        Assert.Equal(10, second.UploadBytesPerSecond);
        Assert.Equal(20, second.TotalUpload);
        Assert.Equal(40, second.TotalDownload);
        Assert.Contains("/xray.app.stats.command.StatsService/QueryStats", invoker.Paths);
    }

    [Fact]
    public async Task Reader_returns_null_when_no_tunnel_counters()
    {
        var invoker = new FakeStatsInvoker(Response(("outbound>>>direct>>>traffic>>>uplink", 3)));
        var reader = new XrayTrafficReader(Client(invoker), ["proxy"]);
        Assert.Null(await reader.ReadAsync());
    }

    [Fact]
    public void Single_profile_build_emits_stats_api_and_policy()
    {
        var settings = new AppSettings { Mode = ConnectionMode.LocalProxy };
        var profile = new Profile { Protocol = ProtocolKind.Vless, Host = "vpn.invalid", Port = 443 };
        var json = new XrayConfigBuilder().Build(profile, new ProfileSecrets(), new BuildContext(settings, 20901));
        Assert.Equal("127.0.0.1:20901", (string?)json["api"]!["listen"]);
        Assert.Equal("StatsService", (string?)json["api"]!["services"]![0]);
        Assert.NotNull(json["stats"]);
        Assert.True((bool)json["policy"]!["system"]!["statsOutboundUplink"]!);
        Assert.True((bool)json["policy"]!["system"]!["statsOutboundDownlink"]!);
    }

    [Fact]
    public async Task Xray_engine_writes_api_config_for_single_profiles()
    {
        var runner = new FakeProcessRunner { StartupOutput = { "I am Xray, started" } };
        await using var engine = new XrayEngine(runner, new LogRedactor(),
            new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64)));
        var work = TestDirs.New("xray-stats");
        var profile = new Profile { Protocol = ProtocolKind.Vless, Host = "vpn.invalid", Port = 443 };
        await engine.StartAsync(new EngineStartRequest
        {
            Profile = profile,
            Secrets = new ProfileSecrets(),
            Settings = new AppSettings(),
            WorkDirectory = work,
        });
        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(work, "xray.json")))!;
        Assert.NotNull(config["stats"]);
        Assert.Equal("StatsService", (string?)config["api"]!["services"]![0]);
        Assert.StartsWith("127.0.0.1:", (string?)config["api"]!["listen"], StringComparison.Ordinal);
        await engine.StopAsync();
        Assert.True(runner.Last!.Stopped);
    }
}
