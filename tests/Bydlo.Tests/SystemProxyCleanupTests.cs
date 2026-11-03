using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

/// <summary>Covers issue #208: the system proxy never stays on a dead port.</summary>
public sealed class SystemProxyCleanupTests
{
    private static (ConnectionManager Manager, FakeProcessRunner Runner, RecordingProxy Proxy) Create(
        ISystemProxyService? proxy = null, IPortChecker? ports = null)
    {
        var runner = new FakeProcessRunner();
        var engine = new RecordingEngine(runner);
        var registry = new EngineRegistry(() => throw new InvalidOperationException())
            .Register(engine.Descriptor, () => engine);
        var recording = proxy as RecordingProxy ?? new RecordingProxy();
        var manager = new ConnectionManager(registry, new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("system-proxy-cleanup")), new ConflictDetector(ports ?? new FakePorts()),
            new LogHub(new LogRedactor()), recording, OsFamily.Windows)
        {
            // Isolated tests never touch the real OS proxy; bypass the development safety lock like SystemProxyPortTests.
            EnsureSystemProxyAllowed = () => { },
        };
        return (manager, runner, recording);
    }

    private static Profile Demo() => new() { Name = "demo", Protocol = ProtocolKind.Trojan, Host = "demo.invalid", Port = 443 };

    [Fact]
    public async Task Engine_crash_clears_system_proxy()
    {
        var (manager, runner, proxy) = Create();
        await using var _ = manager;
        var settings = new AppSettings { LocalProxyPort = 2345, SetSystemProxy = true, AutoReconnect = false };
        await manager.ConnectAsync(Demo(), settings);
        Assert.Equal(ConnectionStatus.Connected, manager.Primary.Status);
        Assert.Single(proxy.Applied);
        runner.Last!.Crash(1);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (manager.Primary.Status != ConnectionStatus.Error && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }
        Assert.Equal(ConnectionStatus.Error, manager.Primary.Status);
        until = DateTime.UtcNow.AddSeconds(5);
        while (proxy.Clears == 0 && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }
        Assert.Equal(1, proxy.Clears);
    }

    [Fact]
    public async Task Failed_system_proxy_setup_stops_session()
    {
        var throwing = new ThrowingProxy();
        var runner = new FakeProcessRunner();
        var engine = new RecordingEngine(runner);
        var registry = new EngineRegistry(() => throw new InvalidOperationException())
            .Register(engine.Descriptor, () => engine);
        await using var manager = new ConnectionManager(registry, new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("system-proxy-fail")), new ConflictDetector(new FakePorts()),
            new LogHub(new LogRedactor()), throwing, OsFamily.Windows)
        {
            EnsureSystemProxyAllowed = () => { },
        };
        var settings = new AppSettings { LocalProxyPort = 2345, SetSystemProxy = true, AutoReconnect = false };
        var ex = await Assert.ThrowsAsync<EngineException>(() => manager.ConnectAsync(Demo(), settings));
        Assert.Equal("error.systemproxy.failed", ex.LocalizationKey);
        Assert.Equal(ConnectionStatus.Disconnected, manager.Primary.Status);
        Assert.True(runner.Last!.Stopped);
    }

    [Fact]
    public async Task Mock_drop_clears_proxy_when_set()
    {
        var registry = new EngineRegistry(() => new MockEngine(seed: 1) { TimeScale = 0.01 });
        var proxy = new RecordingProxy();
        await using var manager = new ConnectionManager(registry, new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("mock-drop-proxy")), new ConflictDetector(new FakePorts()),
            new LogHub(new LogRedactor()), proxy, OsFamily.Windows);
        await manager.ConnectAsync(Demo(), new AppSettings { MockMode = true, SetSystemProxy = true, AutoReconnect = false, MockScenario = MockScenario.DropAfterConnect });
        Assert.Equal(ConnectionStatus.Connected, manager.Primary.Status);
        Assert.Empty(proxy.Applied);
    }

    private sealed class RecordingProxy : ISystemProxyService
    {
        public bool IsSupported => true;
        public List<(string Host, int Port)> Applied { get; } = [];
        public int Clears { get; private set; }
        public Task SetAsync(string host, int port, CancellationToken ct = default)
        {
            Applied.Add((host, port));
            return Task.CompletedTask;
        }
        public Task ClearAsync(CancellationToken ct = default)
        {
            Clears++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingProxy : ISystemProxyService
    {
        public bool IsSupported => true;
        public Task SetAsync(string host, int port, CancellationToken ct = default) =>
            Task.FromException(new IOException("System proxy failed"));
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingEngine(IProcessRunner runner)
        : ProcessEngineBase(runner, new LogRedactor())
    {
        protected override TimeSpan ReadyTimeout => TimeSpan.Zero;
        public override EngineDescriptor Descriptor { get; } = new()
        {
            Kind = EngineKind.SingBox, DisplayName = "Fake", Upstream = "", License = "",
            Protocols = new HashSet<ProtocolKind> { ProtocolKind.Trojan },
            Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented },
        };
        public override bool CanHandle(Profile profile) => true;
        public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) =>
            Task.FromResult(EngineAvailability.Ready("fake"));
        protected override Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct) =>
            Task.FromResult(new ProcessSpec { FileName = "fake" });
    }
}
