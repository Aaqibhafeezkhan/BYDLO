using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

public class EngineCleanupTests
{
    private static EngineStartRequest Request() => new()
    {
        Settings = new AppSettings(), WorkDirectory = TestDirs.New("cleanup"),
    };

    [Fact]
    public async Task Firewall_cleanup_failure_still_stops_process_and_can_be_retried()
    {
        var runner = new FakeProcessRunner();
        var engine = new CleanupEngine(runner, EngineKind.Zapret);
        await engine.StartAsync(Request());
        engine.FailCleanup = true;
        await Assert.ThrowsAsync<AggregateException>(() => engine.StopAsync());
        Assert.True(runner.Last!.Stopped);
        Assert.Equal(EngineState.Failed, engine.State);
        engine.FailCleanup = false;
        await engine.StopAsync();
        Assert.Equal(EngineState.Stopped, engine.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_stop_or_dispose_keeps_process_for_retry(bool dispose)
    {
        var runner = new FakeProcessRunner();
        var engine = new CleanupEngine(runner, EngineKind.Zapret);
        await engine.StartAsync(Request());
        var process = runner.Last!;
        process.StopError = new IOException("Access denied during stop");
        await Assert.ThrowsAsync<AggregateException>(async () =>
        {
            if (dispose) { await engine.DisposeAsync(); }
            else { await engine.StopAsync(); }
        });
        Assert.False(process.HasExited);
        await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(Request()));
        process.StopError = null;
        await engine.StopAsync();
        Assert.True(process.Stopped);
        Assert.Single(runner.Started);
    }

    [Fact]
    public async Task Failed_start_with_failed_cleanup_retains_session_for_stop()
    {
        var runner = new FakeProcessRunner();
        var engine = new CleanupEngine(runner, EngineKind.Zapret) { FailStart = true, FailCleanup = true };
        var registry = new EngineRegistry(() => throw new InvalidOperationException()).Register(engine.Descriptor, () => engine);
        await using var manager = new ConnectionManager(registry, new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("cleanup-start")), new ConflictDetector(new FakePorts()),
            new LogHub(new LogRedactor()), os: OsFamily.Windows);
        var error = await Assert.ThrowsAsync<EngineException>(() => manager.StartZapretAsync(new AppSettings()));
        Assert.Equal("error.engine.cleanup", error.LocalizationKey);
        Assert.True(manager.Zapret.CleanupPending);
        Assert.True(runner.Last!.Stopped);
        engine.FailCleanup = false;
        await manager.StopToolAsync(ConnectionManager.ZapretKey);
        Assert.Equal(ConnectionStatus.Disconnected, manager.Zapret.Status);
    }

    [Fact]
    public async Task Shutdown_attempts_all_tools_and_retains_failed_session_until_retry()
    {
        var primary = new CleanupEngine(new FakeProcessRunner(), EngineKind.SingBox);
        var zapret = new CleanupEngine(new FakeProcessRunner(), EngineKind.Zapret);
        var telegram = new CleanupEngine(new FakeProcessRunner(), EngineKind.TgWsProxy);
        var registry = new EngineRegistry(() => throw new InvalidOperationException());
        foreach (var engine in new[] { primary, zapret, telegram })
        {
            registry.Register(engine.Descriptor, () => engine);
        }
        await using var manager = new ConnectionManager(registry, new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("cleanup-manager")), new ConflictDetector(new FakePorts()),
            new LogHub(new LogRedactor()), os: OsFamily.Windows);
        var settings = new AppSettings();
        await manager.ConnectAsync(new Profile { Protocol = ProtocolKind.Trojan, Host = "fake.invalid", Port = 443 }, settings);
        await manager.StartZapretAsync(settings);
        await manager.StartTgWsProxyAsync(settings);
        primary.FailCleanup = true;
        zapret.FailCleanup = true;
        await Assert.ThrowsAsync<AggregateException>(() => manager.StopAllAsync());
        Assert.Equal(ConnectionStatus.Error, manager.Primary.Status);
        Assert.Equal(ConnectionStatus.Error, manager.Zapret.Status);
        Assert.True(manager.Zapret.CleanupPending);
        Assert.Equal("error.engine.cleanup", manager.Zapret.ErrorLocalizationKey);
        Assert.Equal(ConnectionStatus.Disconnected, manager.TgWsProxy.Status);
        primary.FailCleanup = false;
        zapret.FailCleanup = false;
        await manager.StopAllAsync();
        Assert.Equal(ConnectionStatus.Disconnected, manager.Primary.Status);
        Assert.Equal(ConnectionStatus.Disconnected, manager.Zapret.Status);
        Assert.False(manager.Zapret.CleanupPending);
    }

    private sealed class CleanupEngine(IProcessRunner runner, EngineKind kind)
        : ProcessEngineBase(runner, new LogRedactor())
    {
        public bool FailCleanup { get; set; }
        public bool FailStart { get; set; }
        protected override TimeSpan ReadyTimeout => TimeSpan.Zero;
        public override EngineDescriptor Descriptor { get; } = new()
        {
            Kind = kind, DisplayName = "Fake cleanup engine", License = "", Upstream = "",
            IsTool = kind is EngineKind.Zapret or EngineKind.TgWsProxy,
            Protocols = new HashSet<ProtocolKind> { ProtocolKind.Trojan },
            Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented },
        };
        public override bool CanHandle(Profile profile) => true;
        public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) =>
            Task.FromResult(EngineAvailability.Ready("fake"));
        protected override Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct) =>
            Task.FromResult(new ProcessSpec { FileName = "fake", Arguments = [] });
        protected override Task OnStoppingAsync() => FailCleanup
            ? Task.FromException(new IOException("Firewall cleanup failed")) : Task.CompletedTask;
        protected override Task OnStartedAsync(EngineStartRequest request, CancellationToken ct) => FailStart
            ? Task.FromException(new IOException("Startup failed")) : Task.CompletedTask;
    }
}
