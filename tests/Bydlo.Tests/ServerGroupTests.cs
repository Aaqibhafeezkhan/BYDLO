using System.Net;
using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bydlo.Core.Config;
using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Bydlo.Engines.Groups;
using Bydlo.Engines.Groups.Rpc;
using Google.Protobuf;
using Grpc.Net.Client;

namespace Bydlo.Tests;

public sealed class ServerGroupTests
{
    private static ChainHop Member(string name) => new(new Profile
    {
        Name = name, Host = name + ".invalid", Port = 443, Protocol = ProtocolKind.Trojan,
    }, new ProfileSecrets { Values = new() { [Keys.Password] = "fake-" + name } });
    private static readonly ChainHop A = Member("a");
    private static readonly ChainHop B = Member("b");
    private static ServerGroup Group(EngineKind engine = EngineKind.SingBox, ServerGroupMode mode = ServerGroupMode.UrlTest) => new()
    {
        Name = "Test", Engine = engine, Mode = mode, ProfileIds = [A.Profile.Id, B.Profile.Id],
    };

    [Theory]
    [InlineData(ServerGroupMode.Manual, "selector")]
    [InlineData(ServerGroupMode.UrlTest, "urltest")]
    [InlineData(ServerGroupMode.Fallback, "selector")]
    public void Singbox_groups_route_dns_and_traffic_through_the_group(ServerGroupMode mode, string type)
    {
        var group = Group(mode: mode);
        group.SelectedProfileId = B.Profile.Id;
        var root = new SingBoxConfigBuilder().BuildGroup(group, [A, B], new(new AppSettings(), 1234, "fake-secret"));
        var selector = root["outbounds"]![0]!;
        Assert.Equal(type, (string?)selector["type"]);
        Assert.Equal("proxy", (string?)root["route"]!["final"]);
        Assert.Equal("proxy", (string?)root["dns"]!["servers"]![0]!["detour"]);
        Assert.Equal(new[] { ServerGroup.Tag(A.Profile.Id), ServerGroup.Tag(B.Profile.Id) },
            selector["outbounds"]!.AsArray().Select(n => (string)n!).ToArray());
        if (mode == ServerGroupMode.UrlTest)
        {
            Assert.Equal(group.ProbeUrl, (string?)selector["url"]);
            Assert.Equal("180s", (string?)selector["interval"]);
            Assert.Equal(50, (int?)selector["tolerance"]);
        }
        else { Assert.Equal(ServerGroup.Tag(B.Profile.Id), (string?)selector["default"]); }
        Assert.Equal("127.0.0.1:1234", (string?)root["experimental"]!["clash_api"]!["external_controller"]);
    }

    [Theory]
    [InlineData(ServerGroupMode.Manual)]
    [InlineData(ServerGroupMode.UrlTest)]
    [InlineData(ServerGroupMode.Fallback)]
    public void Xray_groups_use_balancers_and_observatory_without_direct_fallback(ServerGroupMode mode)
    {
        var group = Group(EngineKind.Xray, mode);
        group.SelectedProfileId = B.Profile.Id;
        var settings = new AppSettings();
        settings.Routing.ProxyDomains.Add("example.invalid");
        var root = new XrayConfigBuilder().BuildGroup(group, [A, B], new(settings, 1234));
        var balancer = root["routing"]!["balancers"]![0]!;
        Assert.Equal("leastPing", (string?)balancer["strategy"]!["type"]);
        Assert.Equal(mode == ServerGroupMode.Manual ? ServerGroup.Tag(B.Profile.Id) : "group-unavailable", (string?)balancer["fallbackTag"]);
        Assert.Equal("blackhole", (string?)root["outbounds"]![0]!["protocol"]);
        Assert.Equal(group.ProbeUrl, (string?)root["observatory"]!["probeUrl"]);
        Assert.Equal("180s", (string?)root["observatory"]!["probeInterval"]);
        Assert.DoesNotContain(root["routing"]!["rules"]!.AsArray().OfType<JsonObject>(), r => (string?)r["outboundTag"] == "proxy");
        Assert.Equal("proxy", (string?)root["routing"]!["rules"]!.AsArray().Last()!["balancerTag"]);
        Assert.Contains("ObservatoryService", root["api"]!["services"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Xray_fragment_dialers_are_excluded_from_prefix_selectors()
    {
        var settings = new AppSettings { DpiDefaults = new() { Fragment = "tls" } };
        var root = new XrayConfigBuilder().BuildGroup(Group(EngineKind.Xray), [A, B], new(settings));
        var auxiliary = root["outbounds"]!.AsArray().OfType<JsonObject>().Where(o => (string?)o["protocol"] == "freedom" && (string?)o["tag"] != "direct").ToList();
        Assert.Equal(2, auxiliary.Count);
        Assert.All(auxiliary, o => Assert.StartsWith("aux-", (string?)o["tag"], StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("file:///tmp/test")]
    [InlineData("https://user:password@example.invalid/")]
    [InlineData("relative")]
    public void Invalid_probe_urls_are_rejected(string url)
    {
        var group = Group(); group.ProbeUrl = url;
        Assert.Throws<EngineException>(() => group.Validate([A.Profile, B.Profile]));
    }

    [Fact]
    public void Invalid_members_and_unsupported_engines_are_rejected()
    {
        var group = Group();
        Assert.Throws<EngineException>(() => group.Validate([]));
        Assert.Throws<EngineException>(() => group.Validate([A.Profile, A.Profile]));
        group.Engine = EngineKind.OpenVpn;
        Assert.Throws<EngineException>(() => group.Validate([A.Profile, B.Profile]));
        group = Group(); group.ProfileIds.Add(Guid.NewGuid());
        Assert.Throws<EngineException>(() => group.Resolve([A.Profile, B.Profile]));
    }

    [Fact]
    public void Subscription_membership_is_resolved_again_after_refresh()
    {
        var subscription = Guid.NewGuid();
        var a = Member("old"); a.Profile.SubscriptionId = subscription;
        var b = Member("new"); b.Profile.SubscriptionId = subscription;
        var group = Group(); group.SubscriptionId = subscription; group.SelectedProfileId = a.Profile.Id;
        Assert.Equal(a.Profile.Id, Assert.Single(group.Resolve([a.Profile])).Id);
        var resolved = group.Resolve([b.Profile, A.Profile]);
        Assert.Equal(b.Profile.Id, Assert.Single(resolved).Id);
        group.Validate(resolved);
        Assert.Equal(b.Profile.Id, group.InitialProfileId([b]));
    }

    [Fact]
    public void Auto_selection_honors_tolerance_and_failover_is_sticky()
    {
        var group = Group();
        GroupProbe[] probes = [new(A.Profile.Id, true, 120), new(B.Profile.Id, true, 80)];
        Assert.Equal(A.Profile.Id, GroupSelection.Pick(group, A.Profile.Id, probes));
        group.ToleranceMs = 20;
        Assert.Equal(B.Profile.Id, GroupSelection.Pick(group, A.Profile.Id, probes));
        group.Mode = ServerGroupMode.Fallback;
        Assert.Equal(A.Profile.Id, GroupSelection.Pick(group, A.Profile.Id, probes));
        Assert.Equal(B.Profile.Id, GroupSelection.Pick(group, A.Profile.Id, [new(A.Profile.Id, false, 0), probes[1]]));
        Assert.Null(GroupSelection.Pick(group, A.Profile.Id, [new(A.Profile.Id, false, 0), new(B.Profile.Id, false, 0)]));
    }

    [Fact]
    public async Task Monitor_confirms_selection_and_clears_it_when_all_members_fail()
    {
        var group = Group(EngineKind.Xray, ServerGroupMode.Fallback);
        var controller = new FakeController { Probes = [new(A.Profile.Id, true, 100), new(B.Profile.Id, true, 30)] };
        await using var monitor = new GroupMonitor(group, [A, B], controller, false);
        await monitor.TickAsync(default);
        Assert.Equal(A.Profile.Id, monitor.Selected);
        controller.Probes = [new(A.Profile.Id, false, 0), new(B.Profile.Id, true, 30)];
        await monitor.TickAsync(default);
        Assert.Equal(B.Profile.Id, monitor.Selected);
        controller.Probes = [];
        await monitor.TickAsync(default);
        Assert.Null(monitor.Selected);
        Assert.Null(controller.Current);
    }

    [Fact]
    public async Task Native_urltest_status_is_read_from_the_core()
    {
        var controller = new FakeController { Current = B.Profile.Id };
        await using var monitor = new GroupMonitor(Group(), [A, B], controller, true);
        await monitor.TickAsync(default);
        Assert.Equal(B.Profile.Id, monitor.Selected);
        Assert.Equal(0, controller.ProbeCount);
    }

    [Fact]
    public async Task Clash_controller_authenticates_and_maps_current_member_without_network()
    {
        using var handler = new ClashHandler();
        using var http = new HttpClient(handler);
        var controller = new ClashGroupController(http, 1234, "fake-token", [A, B]);
        Assert.Equal(B.Profile.Id, await controller.CurrentAsync(default));
        await controller.SelectAsync(A.Profile.Id, default);
        var probes = await controller.ProbeAsync(Group(), [A, B], default);
        Assert.All(probes, p => Assert.True(p.Alive));
        Assert.Equal(4, handler.Requests);
    }

    [Fact]
    public async Task Mock_group_connection_snapshots_members_and_settings_and_rejects_stale_ids()
    {
        var registry = new EngineRegistry(() => new MockEngine { TimeScale = .001 });
        await using var manager = new ConnectionManager(registry, new InMemorySecretStore(), new RootedAppPaths(TestDirs.New("groups")),
            new ConflictDetector(new FakePorts()), new LogHub(new LogRedactor()), os: OsFamily.Windows);
        var group = Group(); group.SelectedProfileId = B.Profile.Id;
        await manager.ConnectGroupAsync(group, [A, B], new AppSettings { MockMode = true });
        Assert.True(manager.Primary.IsMock);
        Assert.Equal(B.Profile.Id, manager.Primary.SelectedGroupProfile?.Id);
        Assert.Equal(B.Profile.Id, manager.Primary.Profile?.Id);
        group.Name = "edited";
        Assert.Equal("Test", manager.Primary.Group?.Name);
        var bad = Group(); bad.ProfileIds.Reverse();
        await Assert.ThrowsAsync<EngineException>(() => manager.ConnectGroupAsync(bad, [A, B], new AppSettings { MockMode = true }));
        Assert.Equal(ConnectionStatus.Connected, manager.Primary.Status);
        await manager.DisconnectAsync();
        Assert.Null(manager.Primary.SelectedGroupProfile);
    }

    [Fact]
    public void Settings_roundtrip_keeps_group_references_without_credentials()
    {
        var settings = new AppSettings { ServerGroups = [Group()] };
        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(A.Profile.Id, restored.ServerGroups[0].ProfileIds[0]);
        Assert.DoesNotContain("fake-a", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Xray_grpc_reads_health_confirms_target_and_fails_closed_without_sockets()
    {
        using var handler = new XrayHandler();
        using var http = new HttpClient(handler);
        using var channel = GrpcChannel.ForAddress("http://127.0.0.1:1234", new GrpcChannelOptions { HttpClient = http });
        var controller = new XrayGroupController(channel, [A, B]);
        var probes = await controller.ProbeAsync(Group(EngineKind.Xray), [A, B], default);
        Assert.True(probes[0].Alive);
        Assert.False(probes[1].Alive);
        await controller.SelectAsync(A.Profile.Id, default);
        Assert.Equal(A.Profile.Id, await controller.CurrentAsync(default));
        await controller.SelectAsync(null, default);
        Assert.Null(await controller.CurrentAsync(default));
        Assert.Equal("group-unavailable", handler.Target);
    }

    [Fact]
    public void Helper_validates_every_group_member_and_rejects_plugin_profiles()
    {
        var group = Group();
        var member = Member("plugin"); member.Profile.Options[Keys.Plugin] = "unsafe";
        var request = new HelperRequest
        {
            Engine = EngineKind.SingBox, Operation = HelperOperation.Start,
            Start = new EngineStartRequest { Profile = A.Profile, Group = group, GroupMembers = [A, B], Settings = new(), WorkDirectory = "" },
        };
        HelperRequestPolicy.Validate(request);
        var unsafeRequest = request with { Start = request.Start with { GroupMembers = [A, member] } };
        Assert.Throws<InvalidDataException>(() => HelperRequestPolicy.Validate(unsafeRequest));
    }

    private sealed class XrayHandler : HttpMessageHandler
    {
        public string Target { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            var payload = bytes.AsMemory(5).ToArray();
            IMessage reply;
            switch (request.RequestUri.AbsolutePath)
            {
                case "/xray.core.app.observatory.command.ObservatoryService/GetOutboundStatus":
                    reply = new GetOutboundStatusResponse
                    {
                        Status = new ObservationResult
                        {
                            Status = { new OutboundStatus { Alive = true, Delay = 50, OutboundTag = ServerGroup.Tag(A.Profile.Id), LastTryTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                                new OutboundStatus { Alive = true, Delay = 10, OutboundTag = ServerGroup.Tag(B.Profile.Id), LastTryTime = 1 } },
                        },
                    };
                    break;
                case "/xray.app.router.command.RoutingService/OverrideBalancerTarget":
                    var change = OverrideBalancerTargetRequest.Parser.ParseFrom(payload);
                    Assert.Equal("proxy", change.BalancerTag); Target = change.Target;
                    reply = new OverrideBalancerTargetResponse();
                    break;
                case "/xray.app.router.command.RoutingService/GetBalancerInfo":
                    Assert.Equal("proxy", GetBalancerInfoRequest.Parser.ParseFrom(payload).Tag);
                    reply = new GetBalancerInfoResponse { Balancer = new BalancerMsg { Override = new OverrideInfo { Target = Target } } };
                    break;
                default: throw new InvalidOperationException("Unexpected RPC method.");
            }
            var body = reply.ToByteArray();
            var frame = new byte[body.Length + 5];
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), body.Length); body.CopyTo(frame, 5);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Version = HttpVersion.Version20, Content = new ByteArrayContent(frame) };
            response.Content.Headers.ContentType = new("application/grpc");
            response.TrailingHeaders.Add("grpc-status", "0");
            return response;
        }
    }

    private sealed class FakeController : IGroupController
    {
        public Guid? Current { get; set; }
        public IReadOnlyList<GroupProbe> Probes { get; set; } = [];
        public int ProbeCount { get; private set; }
        public Task<IReadOnlyList<GroupProbe>> ProbeAsync(ServerGroup group, IReadOnlyList<ChainHop> members, CancellationToken ct)
        { ProbeCount++; return Task.FromResult(Probes); }
        public Task SelectAsync(Guid? id, CancellationToken ct) { Current = id; return Task.CompletedTask; }
        public Task<Guid?> CurrentAsync(CancellationToken ct) => Task.FromResult(Current);
    }

    private sealed class ClashHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("fake-token", request.Headers.Authorization?.Parameter);
            if (request.Method == HttpMethod.Put)
            {
                var body = await request.Content!.ReadFromJsonAsync<JsonObject>(ct);
                Assert.Equal(ServerGroup.Tag(A.Profile.Id), (string?)body?["name"]);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { now = ServerGroup.Tag(B.Profile.Id), delay = 42 }),
            };
        }
    }
}
