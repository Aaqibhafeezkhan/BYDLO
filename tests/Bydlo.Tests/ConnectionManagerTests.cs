using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Bydlo.Engines.SingBox;

namespace Bydlo.Tests;

public class ConnectionManagerTests
{
    private static (ConnectionManager Manager, List<SessionSnapshot> Events) Create(IPortChecker? ports = null, double timeScale = 0.01)
    {
        var registry = new EngineRegistry(() => new MockEngine(seed: 1) { TimeScale = timeScale });
        var manager = new ConnectionManager(registry, new InMemorySecretStore(), new RootedAppPaths(TestDirs.New("cm")),
            new ConflictDetector(ports ?? new FakePorts()), new LogHub(new LogRedactor()), os: OsFamily.Windows)
        {
            ReconnectDelay = _ => TimeSpan.FromMilliseconds(10),
        };
        var events = new List<SessionSnapshot>();
        manager.SessionChanged += (_, s) => { lock (events) { events.Add(s); } };
        return (manager, events);
    }

    private static Profile Demo() => new() { Name = "demo", Protocol = ProtocolKind.Vless, Host = "demo.invalid", Port = 443 };

    private static async Task WaitFor(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition not reached in time");
    }

    [Fact]
    public async Task Active_connections_follow_the_mock_session_lifecycle()
    {
        var (manager, _) = Create();
        await using var cleanup = manager;
        Assert.Null(await manager.ListActiveConnectionsAsync());
        await manager.ConnectAsync(Demo(), new AppSettings { MockMode = true });
        var connection = Assert.Single((await manager.ListActiveConnectionsAsync())!);
        await manager.CloseActiveConnectionAsync("unknown-id");
        Assert.Single((await manager.ListActiveConnectionsAsync())!);
        await manager.CloseActiveConnectionAsync(connection.Id);
        Assert.Empty((await manager.ListActiveConnectionsAsync())!);
        await manager.DisconnectAsync();
        Assert.Null(await manager.ListActiveConnectionsAsync());
        await manager.ConnectAsync(Demo(), new AppSettings { MockMode = true });
        Assert.Single((await manager.ListActiveConnectionsAsync())!);
    }

    [Fact]
    public async Task Mock_connect_produces_traffic_and_disconnects()
    {
        var (m, _) = Create();
        await m.ConnectAsync(Demo(), new AppSettings { MockMode = true });
        Assert.Equal(ConnectionStatus.Connected, m.Primary.Status);
        Assert.True(m.Primary.IsMock);
        await WaitFor(() => m.Primary.LastTraffic is not null);

        await m.DisconnectAsync();
        Assert.Equal(ConnectionStatus.Disconnected, m.Primary.Status);
    }

    [Fact]
    public async Task Snapshot_captures_mode_and_port_when_engine_starts()
    {
        var (m, _) = Create();
        var settings = new AppSettings { MockMode = true, Mode = ConnectionMode.LocalProxy, LocalProxyPort = 2345 };

        await m.ConnectAsync(Demo(), settings);
        Assert.Equal(ConnectionMode.LocalProxy, m.Primary.Mode);
        Assert.Equal(2345, m.Primary.LocalProxyPort);

        settings.Mode = ConnectionMode.Tun;
        settings.LocalProxyPort = 3456;

        Assert.Equal(ConnectionMode.LocalProxy, m.Primary.Mode);
        Assert.Equal(2345, m.Primary.LocalProxyPort);
        await m.DisconnectAsync();
    }

    [Fact]
    public async Task Mock_failure_is_reported_as_error()
    {
        var (m, _) = Create();
        await Assert.ThrowsAsync<EngineException>(() => m.ConnectAsync(Demo(), new AppSettings { MockMode = true, MockScenario = MockScenario.FailOnConnect }));
        Assert.Equal(ConnectionStatus.Error, m.Primary.Status);
        Assert.NotNull(m.Primary.Profile);
        Assert.False(m.Primary.HoldsResources);
        Assert.Contains("mock", m.Primary.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Crash_triggers_auto_reconnect()
    {
        var (m, events) = Create(timeScale: 0.002);
        await m.ConnectAsync(Demo(), new AppSettings { MockMode = true, MockScenario = MockScenario.DropAfterConnect, AutoReconnect = true });
        await WaitFor(() => { lock (events) { return events.Any(e => e.Status == ConnectionStatus.Reconnecting); } });
        await m.DisconnectAsync();
    }

    [Fact]
    public async Task Tools_run_independently_of_the_primary_tunnel()
    {
        var (m, _) = Create();
        var s = new AppSettings { MockMode = true };
        await m.StartTgWsProxyAsync(s);
        await m.ConnectAsync(Demo(), s);
        Assert.Equal(ConnectionStatus.Connected, m.TgWsProxy.Status);
        Assert.Equal(ConnectionStatus.Connected, m.Primary.Status);
        await m.StopAllAsync();
        Assert.Equal(ConnectionStatus.Disconnected, m.TgWsProxy.Status);
    }

    [Fact]
    public async Task Busy_local_port_blocks_a_real_connection_before_anything_starts()
    {
        var (m, _) = Create(new FakePorts(2080));
        var ex = await Assert.ThrowsAsync<EngineException>(() => m.ConnectAsync(Demo(), new AppSettings()));
        Assert.Contains("2080", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Conflicts_warn_about_tun_with_zapret_and_disabled_mesh_ranges()
    {
        var detector = new ConflictDetector(new FakePorts());
        var settings = new AppSettings { Mode = ConnectionMode.Tun, Routing = { PreserveMeshVpnRanges = false } };
        var issues = detector.CheckPrimary(Demo(), settings, zapretRunning: true, tgRunning: false);
        Assert.Contains(issues, i => i.Code == "zapret.tun");
        Assert.Contains(issues, i => i.Code == "tun.mesh-vpn");
        Assert.DoesNotContain(issues, i => i.Severity == ConflictSeverity.Blocking);
    }

    [Fact]
    public async Task SingBox_adapter_writes_config_and_starts_only_through_the_runner()
    {
        var runner = new FakeProcessRunner { StartupOutput = { "INFO sing-box started (0.1s)" } };
        var engine = new SingBoxEngine(runner, new LogRedactor(), new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64), "C:/fake/sing-box.exe".Replace('/', Path.DirectorySeparatorChar)), new FakePrivileges(false));
        var work = TestDirs.New("singbox");
        var profile = new Profile { Name = "t", Protocol = ProtocolKind.Trojan, Host = "t.example.com", Port = 443 };

        await engine.StartAsync(new EngineStartRequest
        {
            Profile = profile,
            Secrets = new ProfileSecrets { Values = { [Keys.Password] = "pw-123456" } },
            Settings = new AppSettings(),
            WorkDirectory = work,
        });

        Assert.Equal(EngineState.Running, engine.State);
        var spec = Assert.Single(runner.Started);
        Assert.Equal("run", spec.Arguments[0]);
        Assert.True(File.Exists(Path.Combine(work, "sing-box.json")));
        await engine.StopAsync();
        Assert.True(runner.Last!.Stopped);
    }

    [Fact]
    public async Task SingBox_tun_mode_requires_elevation()
    {
        var engine = new SingBoxEngine(new FakeProcessRunner(), new LogRedactor(), new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64)), new FakePrivileges(false));
        var a = await engine.CheckAvailabilityAsync(new AppSettings { Mode = ConnectionMode.Tun });
        Assert.Equal(AvailabilityStatus.NeedsElevation, a.Status);
    }

    [Fact]
    public async Task Unexpected_engine_exit_is_reported()
    {
        var runner = new FakeProcessRunner { StartupOutput = { "sing-box started" } };
        var engine = new SingBoxEngine(runner, new LogRedactor(), new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64)), new FakePrivileges(true));
        var states = new List<EngineStateChangedEventArgs>();
        engine.StateChanged += (_, e) => states.Add(e);
        await engine.StartAsync(new EngineStartRequest
        {
            Profile = new Profile { Protocol = ProtocolKind.Socks5, Host = "s.example.com", Port = 1080 },
            Settings = new AppSettings(),
            WorkDirectory = TestDirs.New("crash"),
        });
        runner.Last!.Crash(3);
        Assert.Equal(EngineState.Failed, engine.State);
        Assert.Contains("code 3", states[^1].Error, StringComparison.Ordinal);
    }
}
