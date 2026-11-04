using Bydlo.Platform.Processes;
using System.Text.Json.Nodes;
using Bydlo.Core.Config;
using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Engines.SingBox;

namespace Bydlo.Tests;

public class KillSwitchTests
{
    private static AppSettings Settings() => new() { Mode = ConnectionMode.Tun, KillSwitch = true };
    private static EngineStartRequest Request(AppSettings? settings = null) => new()
    {
        Settings = settings ?? Settings(), WorkDirectory = "session",
        Profile = new Profile { Protocol = ProtocolKind.Trojan, Host = "vpn.invalid", Port = 443 },
    };

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Config_preserves_only_selected_lan_and_mesh_exceptions(bool lan, bool mesh)
    {
        var s = Settings();
        s.Routing.BypassPrivateNetworks = lan;
        s.Routing.PreserveMeshVpnRanges = mesh;
        s.Routing.DirectCidrs = ["0.0.0.0/0", "::/0"];
        s.Routing.DirectDomains = ["leak.invalid"];
        s.Routing.DirectProcesses = ["browser"];
        s.Routing.ProxyOnlyListed = true;
        s.Routing.ProxyDomains = ["only.invalid"];
        var config = new SingBoxConfigBuilder().BuildKillSwitch(s, 2080);
        var tun = config["inbounds"]![0]!;
        Assert.True((bool)tun["strict_route"]!);
        Assert.True((bool)tun["auto_route"]!);
        Assert.Equal(2, tun["address"]!.AsArray().Count);
        var exclusions = tun["route_exclude_address"]?.AsArray().Select(n => (string?)n).ToArray() ?? [];
        Assert.Equal(lan, exclusions.Contains("192.168.0.0/16"));
        Assert.Equal(mesh, exclusions.Contains("100.64.0.0/10"));
        Assert.Equal(mesh, exclusions.Contains("fd7a:115c:a1e3::/48"));
        Assert.DoesNotContain("0.0.0.0/0", exclusions);
        Assert.DoesNotContain("::/0", exclusions);
        Assert.Equal("proxy", (string?)config["route"]!["final"]);
        Assert.DoesNotContain("leak.invalid", config.ToJsonString());
        Assert.DoesNotContain("browser", config.ToJsonString());
        Assert.DoesNotContain(config["route"]!["rules"]!.AsArray(), n => n?["ip_is_private"] is not null);
        var proxy = config["outbounds"]![0]!;
        Assert.Equal("socks", (string?)proxy["type"]);
        Assert.Equal("127.0.0.1", (string?)proxy["server"]);
        Assert.Equal(2080, (int)proxy["server_port"]!);
        Assert.Equal("proxy", (string?)config["dns"]!["servers"]![0]!["detour"]);
    }

    [Fact]
    public void Config_requires_explicit_opt_in_and_tun()
    {
        var builder = new SingBoxConfigBuilder();
        Assert.Throws<InvalidOperationException>(() => builder.BuildKillSwitch(new AppSettings(), 2080));
        Assert.Throws<InvalidOperationException>(() => builder.BuildKillSwitch(new AppSettings { KillSwitch = true }, 2080));
        Assert.Throws<InvalidOperationException>(() => builder.BuildKillSwitch(new AppSettings { Mode = ConnectionMode.Tun }, 2080));
    }

    [Theory]
    [InlineData(DnsTransport.Udp, "9.9.9.9", "udp")]
    [InlineData(DnsTransport.Tcp, "9.9.9.9", "tcp")]
    [InlineData(DnsTransport.Https, "2606:4700:4700::1111", "https")]
    [InlineData(DnsTransport.Tls, "9.9.9.9", "tls")]
    [InlineData(DnsTransport.Quic, "9.9.9.9", "quic")]
    public void Strict_dns_preserves_transport_and_has_no_direct_fallback(DnsTransport transport, string address, string type)
    {
        var settings = Settings();
        settings.Routing.RemoteDns = address;
        settings.Routing.RemoteDnsTransport = transport;
        settings.Routing.DirectDnsDomains = ["direct.invalid"];
        var config = new SingBoxConfigBuilder().BuildKillSwitch(settings, 2080);
        var dns = config["dns"]!;
        var server = Assert.Single(dns["servers"]!.AsArray())!;
        Assert.Equal(type, (string?)server["type"]);
        Assert.Equal(address, (string?)server["server"]);
        Assert.Equal("proxy", (string?)server["detour"]);
        Assert.Null(server["domain_resolver"]);
        Assert.Equal("remote", (string?)dns["final"]);
        Assert.Equal("remote", (string?)config["route"]!["default_domain_resolver"]);
        Assert.Empty(dns["rules"]!.AsArray());
    }

    [Theory]
    [InlineData(DnsTransport.Tcp, "9.9.9.9", false)]
    [InlineData(DnsTransport.Https, "1.1.1.1", false)]
    [InlineData(DnsTransport.System, "1.1.1.1", true)]
    [InlineData(DnsTransport.Tcp, "local", true)]
    [InlineData(DnsTransport.Https, "", true)]
    [InlineData(DnsTransport.Https, "dns.invalid", true)]
    [InlineData((DnsTransport)999, "1.1.1.1", true)]
    public async Task Unsupported_dns_is_rejected_before_either_process_starts(DnsTransport transport, string address, bool throughProxy)
    {
        var settings = Settings();
        settings.Routing.RemoteDnsTransport = transport;
        settings.Routing.RemoteDns = address;
        settings.Routing.RemoteDnsThroughProxy = throughProxy;
        var error = Assert.Throws<EngineException>(() => new SingBoxConfigBuilder().BuildKillSwitch(settings, 2080));
        Assert.Equal("error.killswitch.dns", error.LocalizationKey);
        var backend = new StubEngine();
        await using var engine = new RetainedTunEngine(backend, () => throw new InvalidOperationException("Guard must not be created."));
        error = await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(Request(settings)));
        Assert.Equal("error.killswitch.dns", error.LocalizationKey);
        Assert.Empty(backend.Starts);
    }

    [Theory]
    [InlineData(DnsTransport.Tcp, false)]
    [InlineData(DnsTransport.Https, true)]
    public async Task Transport_dns_settings_survive_config_generation_and_reconnect(DnsTransport transport, bool directThroughProxy)
    {
        var backend = new StubEngine();
        var guard = new StubEngine();
        await using var engine = new RetainedTunEngine(backend, () => guard);
        var request = Request() with { Profile = new Profile { Protocol = ProtocolKind.Socks5, Host = "vpn.invalid", Port = 1080 } };
        request.Settings.Routing.RemoteDns = "9.9.9.9";
        request.Settings.Routing.RemoteDnsTransport = transport;
        request.Settings.Routing.DirectDns = "8.8.8.8";
        request.Settings.Routing.DirectDnsTransport = DnsTransport.Tls;
        request.Settings.Routing.DirectDnsThroughProxy = directThroughProxy;
        await engine.StartAsync(request);
        request.Settings.Routing.RemoteDnsTransport = DnsTransport.System;
        request.Settings.Routing.RemoteDnsThroughProxy = false;
        request.Settings.Routing.DirectDnsTransport = DnsTransport.Udp;
        request.Settings.Routing.DirectDnsThroughProxy = !directThroughProxy;
        await engine.ReconnectAsync(request);
        Assert.Equal(2, backend.Starts.Count);
        Assert.All(backend.Starts, start =>
        {
            var config = new SingBoxConfigBuilder().Build(start.Profile!, start.Secrets, new BuildContext(start.Settings));
            var remote = config["dns"]!["servers"]![0]!;
            Assert.Equal(transport.ToString().ToLowerInvariant(), (string?)remote["type"]);
            Assert.Equal("9.9.9.9", (string?)remote["server"]);
            Assert.Equal("proxy", (string?)remote["detour"]);
            var direct = config["dns"]!["servers"]![1]!;
            Assert.Equal("tls", (string?)direct["type"]);
            Assert.Equal("8.8.8.8", (string?)direct["server"]);
            Assert.Equal(directThroughProxy ? "proxy" : null, (string?)direct["detour"]);
            Assert.False(start.Settings.Routing.BypassPrivateNetworks);
            Assert.False(start.Settings.Routing.PreserveMeshVpnRanges);
        });
        var guardConfig = new SingBoxConfigBuilder().BuildKillSwitch(Assert.Single(guard.Starts).Settings, 2080);
        Assert.Equal(transport.ToString().ToLowerInvariant(), (string?)guardConfig["dns"]!["servers"]![0]!["type"]);
    }

    [Fact]
    public async Task Failure_and_retries_retain_the_same_tun_and_snapshot_settings()
    {
        var backend = new StubEngine();
        var guard = new StubEngine();
        await using var engine = new RetainedTunEngine(backend, () => guard);
        var request = Request() with { LocalProxyAuth = new LocalProxyAuth("lan-user", "lan-pass") };
        request.Settings.AllowLan = true;
        await engine.StartAsync(request);
        request.Settings.LocalProxyPort = 9999;
        request.Settings.KillSwitch = false;
        backend.Fail();
        backend.FailStart = true;
        await Assert.ThrowsAsync<EngineException>(() => engine.ReconnectAsync(request));
        Assert.Equal(0, guard.Stops);
        Assert.Equal(EngineState.Running, guard.State);
        backend.FailStart = false;
        await engine.ReconnectAsync(request);
        Assert.Single(guard.Starts);
        Assert.All(backend.Starts, r =>
        {
            Assert.Equal(ConnectionMode.LocalProxy, r.Settings.Mode);
            Assert.False(r.Settings.KillSwitch);
            Assert.False(r.Settings.AllowLan);
            Assert.Null(r.LocalProxyAuth);
            Assert.Equal(2080, r.Settings.LocalProxyPort);
        });
        await engine.StopAsync();
        Assert.True(guard.Stops > 0);
        Assert.True(guard.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Initial_failure_releases_tun(bool guardFails)
    {
        var backend = new StubEngine { FailStart = !guardFails };
        var guard = new StubEngine { FailStart = guardFails };
        await using var engine = new RetainedTunEngine(backend, () => guard);
        await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(Request()));
        Assert.True(guard.Disposed);
        Assert.True(guard.Stops > 0);
    }

    [Fact]
    public async Task Stop_failure_still_releases_tun()
    {
        var backend = new StubEngine();
        var guard = new StubEngine();
        var engine = new RetainedTunEngine(backend, () => guard);
        await engine.StartAsync(Request());
        backend.FailStop = true;
        await Assert.ThrowsAsync<EngineException>(() => engine.StopAsync());
        Assert.True(guard.Disposed);
        backend.FailStop = false;
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Guard_crash_is_reported_and_cannot_be_silently_reconnected()
    {
        var backend = new StubEngine();
        var guard = new StubEngine();
        await using var engine = new RetainedTunEngine(backend, () => guard);
        await engine.StartAsync(Request());
        guard.Fail();
        Assert.Equal(EngineState.Failed, engine.State);
        await Assert.ThrowsAsync<EngineException>(() => engine.ReconnectAsync(Request()));
        Assert.Single(backend.Starts);
    }

    [Theory]
    [InlineData(ConnectionMode.LocalProxy, true)]
    [InlineData(ConnectionMode.Tun, false)]
    public async Task Disabled_or_proxy_mode_never_starts_guard(ConnectionMode mode, bool enabled)
    {
        var backend = new StubEngine();
        await using var engine = new RetainedTunEngine(backend, () => throw new InvalidOperationException());
        await engine.StartAsync(Request(new AppSettings { Mode = mode, KillSwitch = enabled }));
        Assert.Single(backend.Starts);
    }

    [Fact]
    public async Task Cancellation_during_start_releases_tun()
    {
        var guard = new StubEngine();
        await using var engine = new RetainedTunEngine(new StubEngine(), () => guard);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.StartAsync(Request(), cts.Token));
        Assert.True(guard.Disposed);
        Assert.True(guard.Stops > 0);
    }

    [Fact]
    public async Task Manager_keeps_guard_after_exhausted_retries_then_releases_on_shutdown()
    {
        var backend = new StubEngine();
        var guard = new StubEngine();
        var engine = new RetainedTunEngine(backend, () => guard);
        var registry = new EngineRegistry(() => throw new InvalidOperationException())
            .Register(backend.Descriptor, () => engine);
        await using var manager = new ConnectionManager(registry, new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("kill-switch-manager")), new ConflictDetector(new FakePorts()),
            new LogHub(new LogRedactor()), os: OsFamily.Windows)
        {
            ReconnectDelay = _ => TimeSpan.FromMilliseconds(1),
        };
        await manager.ConnectAsync(Request().Profile!, Settings());
        backend.FailStart = true;
        backend.Fail();
        for (var i = 0; i < 200 && manager.Primary.Status != ConnectionStatus.Error; i++) { await Task.Delay(10); }
        Assert.Equal(ConnectionStatus.Error, manager.Primary.Status);
        Assert.Equal(4, backend.Starts.Count);
        Assert.Single(guard.Starts);
        Assert.Equal(0, guard.Stops);
        await manager.StopAllAsync();
        Assert.True(guard.Disposed);
        Assert.Equal(ConnectionStatus.Disconnected, manager.Primary.Status);
    }

    [Fact]
    public void Safety_lock_blocks_crash_cleanup_before_inspecting_processes()
    {
        // No real process or network state is inspected or modified.
        NetworkSafetyGuard.OverrideForTests(true);
        try
        {
            var journal = new ProcessJournal(Path.Combine(TestDirs.New("kill-switch-journal"), "journal.json"));
            Assert.Throws<NetworkSafetyException>(() => journal.KillOrphans(RetainedTunEngine.GuardProcessName));
        }
        finally { NetworkSafetyGuard.OverrideForTests(null); }
    }

    [Fact]
    public void Unsupported_engines_cannot_silently_ignore_protection()
    {
        var descriptor = new StubEngine().Descriptor with { Kind = EngineKind.OpenVpn, Protocols = new HashSet<ProtocolKind> { ProtocolKind.OpenVpn } };
        var registry = new EngineRegistry(() => throw new InvalidOperationException())
            .Register(descriptor, () => throw new InvalidOperationException());
        Assert.Throws<EngineException>(() => registry.CreateFor(new Profile { Protocol = ProtocolKind.OpenVpn }, Settings(), OsFamily.Windows));
    }

    [Fact]
    public async Task Proxy_reconnect_uses_original_settings_when_user_enables_kill_switch()
    {
        var backend = new StubEngine();
        await using var engine = new RetainedTunEngine(backend, () => throw new InvalidOperationException());
        var request = Request(new AppSettings());
        await engine.StartAsync(request);
        request.Settings.Mode = ConnectionMode.Tun;
        request.Settings.KillSwitch = true;
        await engine.ReconnectAsync(request);
        Assert.All(backend.Starts, r => Assert.Equal(ConnectionMode.LocalProxy, r.Settings.Mode));
        Assert.All(backend.Starts, r => Assert.False(r.Settings.KillSwitch));
    }

    [Fact]
    public async Task Tun_adapter_generates_guard_config_and_uses_only_fake_runner()
    {
        var runner = new FakeProcessRunner { StartupOutput = { "sing-box started" } };
        await using var guard = new SingBoxTunGuard(runner, new LogRedactor(),
            new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64)), new FakePrivileges(true));
        var request = Request() with { WorkDirectory = TestDirs.New("kill-switch-config") };
        await guard.StartAsync(request);
        var spec = Assert.Single(runner.Started);
        Assert.True(spec.AffectsNetwork);
        Assert.True(spec.GracefulTermination);
        Assert.Equal(RetainedTunEngine.GuardProcessName, spec.DisplayName);
        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(request.WorkDirectory, "tun.json")))!;
        Assert.Equal("tun", (string?)config["inbounds"]![0]!["type"]);
        Assert.Equal("proxy", (string?)config["route"]!["final"]);
        runner.Last!.Crash();
        Assert.Equal(EngineState.Failed, guard.State);
    }

    [Theory]
    [InlineData("fd12:3456::1", true)]
    [InlineData("fd7a:115c:a1e3::1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2606:4700:4700::1111", false)]
    public void Lan_exceptions_cover_ipv6_without_accidentally_including_mesh_or_internet(string address, bool expected)
    {
        var ip = System.Net.IPAddress.Parse(address);
        Assert.Equal(expected, SingBoxConfigBuilder.PrivateNetworkRanges.Any(cidr => System.Net.IPNetwork.Parse(cidr).Contains(ip)));
    }

    [Fact]
    public void Journal_records_owner_and_graceful_cleanup_metadata_without_starting_a_process()
    {
        var journal = new ProcessJournal(Path.Combine(TestDirs.New("kill-switch-owner"), "journal.json"));
        journal.Record(new JournalEntry(RetainedTunEngine.GuardProcessName, 123, "fake", DateTimeOffset.Now, GracefulTermination: true));
        var entry = Assert.Single(journal.Entries());
        Assert.Equal(Environment.ProcessId, entry.OwnerPid);
        Assert.NotNull(entry.OwnerStartedAt);
        Assert.True(entry.GracefulTermination);
    }

    private sealed class StubEngine : IEngine
    {
        public List<EngineStartRequest> Starts { get; } = [];
        public int Stops { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailStart { get; set; }
        public bool FailStop { get; set; }
        public EngineDescriptor Descriptor { get; } = new()
        {
            Kind = EngineKind.SingBox, DisplayName = "fake", License = "", Upstream = "",
            Protocols = new HashSet<ProtocolKind> { ProtocolKind.Trojan },
            Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented },
        };
        public EngineState State { get; private set; }
        public event EventHandler<EngineStateChangedEventArgs>? StateChanged;
        public event EventHandler<string>? Log { add { } remove { } }
        public event EventHandler<TrafficSample>? Traffic { add { } remove { } }
        public bool CanHandle(Profile profile) => true;
        public Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) => Task.FromResult(EngineAvailability.Ready("fake"));
        public Task StartAsync(EngineStartRequest request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Starts.Add(request);
            if (FailStart) { throw new EngineException("start failed"); }
            State = EngineState.Running;
            StateChanged?.Invoke(this, new(State));
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken ct = default)
        {
            Stops++;
            if (FailStop) { throw new EngineException("stop failed"); }
            State = EngineState.Stopped;
            return Task.CompletedTask;
        }
        public void Fail() { State = EngineState.Failed; StateChanged?.Invoke(this, new(State, "fake crash")); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
