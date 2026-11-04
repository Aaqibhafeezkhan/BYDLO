using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

public sealed class ConnectionReplacementTests
{
    private static Profile Demo() => new() { Name = "demo", Protocol = ProtocolKind.Trojan, Host = "demo.invalid", Port = 443 };

    private static Task Connect(ConnectionManager manager, string target, AppSettings settings)
    {
        var first = Demo();
        var last = Demo();
        ChainHop[] members = [new(first, new()), new(last, new())];
        return target switch
        {
            "profile" => manager.ConnectAsync(first, settings),
            "chain" => manager.ConnectChainAsync(new ProxyChain { Name = "chain", ProfileIds = [first.Id, last.Id], PreferredEngine = EngineKind.Xray }, members, settings),
            "group" => manager.ConnectGroupAsync(new ServerGroup { Name = "group", ProfileIds = [first.Id, last.Id], Engine = EngineKind.Xray }, members, settings),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
    }

    private static ConnectionManager Create(EngineRegistry registry, RecordingProxy proxy, Action? ensureAllowed = null) =>
        new(registry, new InMemorySecretStore(), new RootedAppPaths(TestDirs.New("connection-replacement")),
            new ConflictDetector(new FakePorts()), new LogHub(new LogRedactor()), proxy, OsFamily.Windows)
        {
            // Only in-memory proxy services and FakeProcessRunner engines are used by these tests.
            EnsureSystemProxyAllowed = ensureAllowed ?? (() => { }),
        };

    [Theory]
    [InlineData("profile")]
    [InlineData("chain")]
    [InlineData("group")]
    public async Task Invalid_engine_selection_keeps_the_working_session(string target)
    {
        var runner = new FakeProcessRunner();
        var created = 0;
        var descriptor = new RecordingEngine(runner).Descriptor;
        var registry = new EngineRegistry(() => throw new InvalidOperationException())
            .Register(descriptor, () => { created++; return new RecordingEngine(runner); });
        var proxy = new RecordingProxy();
        await using var manager = Create(registry, proxy);
        await manager.ConnectAsync(Demo(), new AppSettings { SetSystemProxy = true });
        var before = manager.Primary;
        var process = runner.Last!;
        var events = new List<SessionSnapshot>();
        manager.SessionChanged += (_, snapshot) => events.Add(snapshot);
        var settings = new AppSettings
        {
            Mode = ConnectionMode.LocalProxy,
            KillSwitch = false,
            Routing = { Preset = RoutingPreset.BlockedOnly },
        };

        var error = await Assert.ThrowsAsync<EngineException>(() => Connect(manager, target, settings));

        Assert.Equal("routing.preset.singbox", error.Message);
        Assert.Equal(before, manager.Primary);
        Assert.False(process.Stopped);
        Assert.Empty(events);
        Assert.Equal(1, created);
        Assert.Equal(0, proxy.Clears);
        Assert.Single(proxy.Applied);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("chain")]
    [InlineData("group")]
    public async Task Kill_switch_with_xray_replaces_the_working_session(string target)
    {
        var runner = new FakeProcessRunner();
        var created = 0;
        var descriptor = new RecordingEngine(runner).Descriptor;
        var registry = new EngineRegistry(() => throw new InvalidOperationException())
            .Register(descriptor, () => { created++; return new RecordingEngine(runner); });
        var proxy = new RecordingProxy();
        await using var manager = Create(registry, proxy);
        await manager.ConnectAsync(Demo(), new AppSettings { SetSystemProxy = true });
        var before = manager.Primary;
        var process = runner.Last!;
        var events = new List<SessionSnapshot>();
        manager.SessionChanged += (_, snapshot) => events.Add(snapshot);

        await Connect(manager, target, new AppSettings { Mode = ConnectionMode.Tun, KillSwitch = true });

        Assert.NotEqual(before, manager.Primary);
        Assert.Equal(ConnectionStatus.Connected, manager.Primary.Status);
        Assert.True(process.Stopped);
        Assert.NotEmpty(events);
        Assert.Equal(2, created);
        Assert.Equal(1, proxy.Clears);
        Assert.Single(proxy.Applied);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("chain")]
    [InlineData("group")]
    public async Task Replacement_engine_is_disposed_when_previous_session_cannot_stop(string target)
    {
        var runner = new FakeProcessRunner();
        var engines = new List<RecordingEngine>();
        var registry = new EngineRegistry(() => throw new InvalidOperationException())
            .Register(new RecordingEngine(runner).Descriptor, () =>
            {
                var engine = new RecordingEngine(runner);
                engines.Add(engine);
                return engine;
            });
        await using var manager = Create(registry, new RecordingProxy());
        await manager.ConnectAsync(Demo(), new AppSettings());
        runner.Last!.StopError = new IOException("Stop failed");

        var error = await Assert.ThrowsAsync<EngineException>(() => Connect(manager, target, new AppSettings()));

        Assert.Equal("error.engine.cleanup", error.LocalizationKey);
        Assert.True(manager.Primary.CleanupPending);
        Assert.Equal(2, engines.Count);
        Assert.True(engines[1].Disposed);
        Assert.Single(runner.Started);
        runner.Last.StopError = null;
        await manager.DisconnectAsync();
    }

    [Theory]
    [InlineData("profile", false)]
    [InlineData("chain", false)]
    [InlineData("group", false)]
    [InlineData("profile", true)]
    [InlineData("chain", true)]
    [InlineData("group", true)]
    public async Task Proxy_failure_stops_session_and_reports_localized_error(string target, bool guardFails)
    {
        var runner = new FakeProcessRunner();
        var engine = new RecordingEngine(runner);
        var registry = new EngineRegistry(() => throw new InvalidOperationException()).Register(engine.Descriptor, () => engine);
        var proxy = new RecordingProxy { FailSet = true };
        await using var manager = Create(registry, proxy, guardFails
            ? () => throw new EngineException("Proxy denied", localizationKey: "error.safety") : null);
        var events = new List<SessionSnapshot>();
        manager.SessionChanged += (_, snapshot) => events.Add(snapshot);

        var error = await Assert.ThrowsAsync<EngineException>(() => Connect(manager, target, new AppSettings { SetSystemProxy = true }));

        var expectedKey = guardFails ? "error.safety" : "error.systemproxy.failed";
        Assert.Equal(expectedKey, error.LocalizationKey);
        Assert.Equal(ConnectionStatus.Disconnected, manager.Primary.Status);
        Assert.True(runner.Last!.Stopped);
        Assert.True(engine.Disposed);
        Assert.Equal(ConnectionStatus.Error, events[^1].Status);
        Assert.Equal(expectedKey, events[^1].ErrorLocalizationKey);
        Assert.False(proxy.Active);
        Assert.Equal(guardFails ? 0 : 1, proxy.Clears);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("chain")]
    [InlineData("group")]
    public async Task Failed_proxy_rollback_is_retained_for_disconnect_retry(string target)
    {
        var runner = new FakeProcessRunner();
        var engine = new RecordingEngine(runner);
        var registry = new EngineRegistry(() => throw new InvalidOperationException()).Register(engine.Descriptor, () => engine);
        var proxy = new RecordingProxy { FailSet = true, FailClear = true };
        await using var manager = Create(registry, proxy);

        await Assert.ThrowsAsync<AggregateException>(() => Connect(manager, target, new AppSettings { SetSystemProxy = true }));

        Assert.Equal(ConnectionStatus.Error, manager.Primary.Status);
        Assert.True(manager.Primary.CleanupPending);
        Assert.Equal("error.engine.cleanup", manager.Primary.ErrorLocalizationKey);
        Assert.True(runner.Last!.Stopped);
        Assert.True(proxy.Active);
        proxy.FailClear = false;
        await manager.DisconnectAsync();
        Assert.False(proxy.Active);
        Assert.Equal(2, proxy.Clears);
        Assert.Equal(ConnectionStatus.Disconnected, manager.Primary.Status);
    }

    private sealed class RecordingProxy : ISystemProxyService
    {
        public bool IsSupported => true;
        public bool FailSet { get; init; }
        public bool FailClear { get; set; }
        public bool Active { get; private set; }
        public List<(string Host, int Port)> Applied { get; } = [];
        public int Clears { get; private set; }
        public Task SetAsync(string host, int port, CancellationToken ct = default)
        {
            Applied.Add((host, port));
            Active = true;
            return FailSet ? Task.FromException(new IOException("Proxy partially applied")) : Task.CompletedTask;
        }
        public Task ClearAsync(CancellationToken ct = default)
        {
            Clears++;
            if (FailClear) { return Task.FromException(new IOException("Proxy rollback failed")); }
            Active = false;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEngine(FakeProcessRunner runner) : ProcessEngineBase(runner, new LogRedactor()), IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        protected override TimeSpan ReadyTimeout => TimeSpan.Zero;
        public override EngineDescriptor Descriptor { get; } = new()
        {
            Kind = EngineKind.Xray, DisplayName = "Fake", Upstream = "", License = "",
            Protocols = new HashSet<ProtocolKind> { ProtocolKind.Trojan },
            Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented },
        };
        public override bool CanHandle(Profile profile) => true;
        public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) =>
            Task.FromResult(EngineAvailability.Ready("fake"));
        protected override Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct) =>
            Task.FromResult(new ProcessSpec { FileName = "fake" });
        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            Disposed = true;
            await base.DisposeAsync();
        }
    }
}
