using System.Text.Json.Nodes;
using Bydlo.Core.Config;
using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Engines;
using Bydlo.Engines.SingBox;
using Bydlo.Engines.Xray;

namespace Bydlo.Tests;

/// <summary>Issue #212: Xray profiles (XHTTP) in TUN run behind a sing-box front with loop avoidance.</summary>
public sealed class XrayTunTests
{
    private static AppSettings TunKillSwitch() => new() { Mode = ConnectionMode.Tun, KillSwitch = true };

    private static EngineStartRequest Request(AppSettings? settings = null) => new()
    {
        Settings = settings ?? TunKillSwitch(),
        WorkDirectory = TestDirs.New("xray-tun"),
        Profile = new Profile { Protocol = ProtocolKind.Vless, Host = "203.0.113.7", Port = 443 },
    };

    [Fact]
    public void Kill_switch_excludes_server_addresses_and_routes_transport_directly()
    {
        var config = new SingBoxConfigBuilder().BuildKillSwitch(TunKillSwitch(), 2080, OsFamily.Windows,
            new KillSwitchExclusions(["203.0.113.7", "2001:db8::1", "198.51.100.0/24", "not-an-address", "example.com"], ["xray", "xray.exe"]));
        var excluded = config["inbounds"]![0]!["route_exclude_address"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Contains("203.0.113.7/32", excluded);
        Assert.Contains("2001:db8::1/128", excluded);
        Assert.Contains("198.51.100.0/24", excluded);
        Assert.DoesNotContain("not-an-address", excluded);
        Assert.DoesNotContain("example.com", excluded);
        var first = config["route"]!["rules"]![0]!;
        Assert.Equal(["xray", "xray.exe"], first["process_name"]!.AsArray().Select(n => (string?)n).ToList());
        Assert.Equal("direct", (string?)first["outbound"]);
        Assert.Equal("proxy", (string?)config["route"]!["final"]);
    }

    [Fact]
    public void Kill_switch_without_exclusions_is_unchanged()
    {
        var config = new SingBoxConfigBuilder().BuildKillSwitch(TunKillSwitch(), 2080);
        Assert.DoesNotContain(config["route"]!["rules"]!.AsArray(), n => n?["process_name"] is not null);
    }

    [Fact]
    public async Task Tun_guard_excludes_xray_server_and_process()
    {
        var runner = new FakeProcessRunner { StartupOutput = { "sing-box started" } };
        await using var guard = new SingBoxTunGuard(runner, new LogRedactor(),
            new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64), "C:/engines/xray.exe".Replace('/', Path.DirectorySeparatorChar)),
            new FakePrivileges(true), EngineKind.Xray);
        var request = Request() with { WorkDirectory = TestDirs.New("xray-tun-guard") };
        await guard.StartAsync(request);
        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(request.WorkDirectory, "tun.json")))!;
        var excluded = config["inbounds"]![0]!["route_exclude_address"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Contains("203.0.113.7/32", excluded);
        var first = config["route"]!["rules"]![0]!;
        Assert.Equal(["xray.exe", "xray"], first["process_name"]!.AsArray().Select(n => (string?)n).ToList());
        Assert.Equal("direct", (string?)first["outbound"]);
        await guard.StopAsync();
    }

    [Fact]
    public async Task Tun_guard_with_domain_server_routes_only_the_process_directly()
    {
        var runner = new FakeProcessRunner { StartupOutput = { "sing-box started" } };
        await using var guard = new SingBoxTunGuard(runner, new LogRedactor(),
            new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64), "/usr/local/bin/xray"),
            new FakePrivileges(true), EngineKind.Xray);
        var request = Request() with
        {
            WorkDirectory = TestDirs.New("xray-tun-guard-domain"),
            Profile = new Profile { Protocol = ProtocolKind.Vless, Host = "xhttp.invalid", Port = 443 },
        };
        await guard.StartAsync(request);
        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(request.WorkDirectory, "tun.json")))!;
        var excluded = config["inbounds"]![0]!["route_exclude_address"]?.AsArray().Select(n => (string?)n).ToList() ?? [];
        Assert.DoesNotContain("xhttp.invalid", excluded);
        var first = config["route"]!["rules"]![0]!;
        Assert.Equal(["xray"], first["process_name"]!.AsArray().Select(n => (string?)n).ToList());
        await guard.StopAsync();
    }

    [Fact]
    public void Tun_guard_without_backend_kind_adds_no_exclusions()
    {
        var guard = new SingBoxTunGuard(new FakeProcessRunner(), new LogRedactor(),
            new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64)), new FakePrivileges(true));
        Assert.Null(guard.TransportExclusions(Request()));
    }

    [Fact]
    public void Registry_wraps_xray_in_retained_tun_for_kill_switch()
    {
        var registry = EngineRegistryFactory.Create(new FakeProcessRunner(), new LogRedactor(),
            new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64)), new FakePrivileges(true));
        var profile = new Profile { Protocol = ProtocolKind.Vless, Host = "203.0.113.7", Port = 443, PreferredEngine = EngineKind.Xray };
        var engine = registry.CreateFor(profile, TunKillSwitch(), OsFamily.Windows);
        var retained = Assert.IsType<RetainedTunEngine>(engine);
        Assert.Equal(EngineKind.Xray, retained.Descriptor.Kind);
    }

    [Fact]
    public async Task Xray_tun_without_kill_switch_stays_not_implemented()
    {
        var engine = new XrayEngine(new FakeProcessRunner(), new LogRedactor(),
            new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64)));
        var availability = await engine.CheckAvailabilityAsync(new AppSettings { Mode = ConnectionMode.Tun });
        Assert.Equal(AvailabilityStatus.NotImplemented, availability.Status);
        Assert.Equal("error.engine.xray.tun", availability.LocalizationKey);
    }

    [Fact]
    public async Task Xray_tun_with_kill_switch_is_ready_for_the_wrapper()
    {
        var engine = new XrayEngine(new FakeProcessRunner(), new LogRedactor(),
            new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64), "C:/engines/xray.exe".Replace('/', Path.DirectorySeparatorChar)));
        var availability = await engine.CheckAvailabilityAsync(TunKillSwitch());
        Assert.Equal(AvailabilityStatus.Ready, availability.Status);
    }

    [Fact]
    public async Task Retained_tun_runs_xray_backend_behind_singbox_guard()
    {
        var backend = new StubBackend();
        var runner = new FakeProcessRunner { StartupOutput = { "sing-box started" } };
        await using var engine = new RetainedTunEngine(backend, () => new SingBoxTunGuard(runner, new LogRedactor(),
            new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64), "C:/engines/xray.exe".Replace('/', Path.DirectorySeparatorChar)),
            new FakePrivileges(true), EngineKind.Xray));
        var request = Request() with { WorkDirectory = TestDirs.New("xray-tun-flow") };
        await engine.StartAsync(request);

        var backendStart = Assert.Single(backend.Starts);
        Assert.Equal(ConnectionMode.LocalProxy, backendStart.Settings.Mode);
        Assert.False(backendStart.Settings.KillSwitch);

        var tunJson = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(request.WorkDirectory, "kill-switch", "tun.json")))!;
        var excluded = tunJson["inbounds"]![0]!["route_exclude_address"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Contains("203.0.113.7/32", excluded);
        Assert.Equal("proxy", (string?)tunJson["route"]!["final"]);
        Assert.Equal("direct", (string?)tunJson["route"]!["rules"]![0]!["outbound"]);

        await engine.StopAsync();
        Assert.True(backend.Stopped);
    }

    private sealed class StubBackend : IEngine
    {
        public List<EngineStartRequest> Starts { get; } = [];
        public bool Stopped { get; private set; }
        public EngineDescriptor Descriptor { get; } = new()
        {
            Kind = EngineKind.Xray, DisplayName = "fake-xray", License = "", Upstream = "",
            Protocols = new HashSet<ProtocolKind> { ProtocolKind.Vless },
            Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented },
        };
        public EngineState State { get; private set; }
        public event EventHandler<EngineStateChangedEventArgs>? StateChanged;
        public event EventHandler<string>? Log { add { } remove { } }
        public event EventHandler<TrafficSample>? Traffic { add { } remove { } }
        public bool CanHandle(Profile profile) => true;
        public Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) =>
            Task.FromResult(EngineAvailability.Ready("fake"));
        public Task StartAsync(EngineStartRequest request, CancellationToken ct = default)
        {
            Starts.Add(request);
            State = EngineState.Running;
            StateChanged?.Invoke(this, new(State));
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken ct = default)
        {
            Stopped = true;
            State = EngineState.Stopped;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

