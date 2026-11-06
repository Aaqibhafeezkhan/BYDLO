using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

/// <summary>Covers issue #202: Best Server reconnects on the same local proxy port without a false conflict.</summary>
public sealed class BestServerPortConflictTests
{
    private static (ConnectionManager Manager, MutablePorts Ports) Create(EngineKind kind = EngineKind.SingBox)
    {
        var runner = new FakeProcessRunner { StartupOutput = { "fake ready" } };
        var engine = new ReconnectableFakeEngine(runner, kind);
        var registry = new EngineRegistry(() => throw new InvalidOperationException())
            .Register(engine.Descriptor, () => engine);
        var ports = new MutablePorts();
        var manager = new ConnectionManager(registry, new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("best-server")), new ConflictDetector(ports),
            new LogHub(new LogRedactor()), os: OsFamily.Windows);
        return (manager, ports);
    }

    private sealed class MutablePorts : Bydlo.Core.Platform.IPortChecker
    {
        public readonly HashSet<int> Busy = [];
        public bool IsTcpPortInUse(int port) => Busy.Contains(port);
    }

    private static Profile Profile(string name) =>
        new() { Name = name, Protocol = ProtocolKind.Trojan, Host = "example.invalid", Port = 443 };

    [Theory]
    [InlineData(EngineKind.SingBox)]
    [InlineData(EngineKind.Xray)]
    public async Task Connect_to_another_profile_on_own_port_succeeds(EngineKind kind)
    {
        var (manager, ports) = Create(kind);
        await using var _ = manager;
        var settings = new AppSettings { LocalProxyPort = 2080, AutoReconnect = false };
        await manager.ConnectAsync(Profile("first"), settings);
        Assert.Equal(ConnectionStatus.Connected, manager.Primary.Status);
        ports.Busy.Add(2080);
        if (kind == EngineKind.Xray) { ports.Busy.Add(2081); }
        await manager.ConnectAsync(Profile("second"), settings);
        Assert.Equal(ConnectionStatus.Connected, manager.Primary.Status);
        Assert.Equal("second", manager.Primary.Profile?.Name);
    }

    [Fact]
    public async Task Connect_chain_on_own_port_succeeds()
    {
        var (manager, ports) = Create();
        await using var _ = manager;
        var settings = new AppSettings { LocalProxyPort = 2080, AutoReconnect = false };
        await manager.ConnectAsync(Profile("first"), settings);
        ports.Busy.Add(2080);
        var first = Profile("hop1");
        var last = Profile("hop2");
        var chain = new ProxyChain { Name = "c", ProfileIds = [first.Id, last.Id] };
        await manager.ConnectChainAsync(chain,
            [new(first, ProfileSecrets.Empty), new(last, ProfileSecrets.Empty)], settings);
        Assert.Equal(ConnectionStatus.Connected, manager.Primary.Status);
        Assert.Equal(chain.Name, manager.Primary.Chain?.Name);
    }

    [Fact]
    public async Task Different_busy_port_still_blocks()
    {
        var (manager, ports) = Create();
        await using var _ = manager;
        var firstSettings = new AppSettings { LocalProxyPort = 2080, AutoReconnect = false };
        await manager.ConnectAsync(Profile("first"), firstSettings);
        var lastPublished = manager.Primary;
        manager.SessionChanged += (_, snapshot) => lastPublished = snapshot;
        ports.Busy.Add(2099);
        var ex = await Assert.ThrowsAsync<EngineException>(() =>
            manager.ConnectAsync(Profile("second"), new AppSettings { LocalProxyPort = 2099 }));
        Assert.Contains("2099", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionStatus.Connected, lastPublished.Status);
        Assert.Equal("first", lastPublished.Profile?.Name);
    }

    [Fact]
    public async Task Tun_session_does_not_own_local_proxy_port()
    {
        var (manager, ports) = Create();
        await using var _ = manager;
        var tunSettings = new AppSettings { Mode = ConnectionMode.Tun, LocalProxyPort = 2080, AutoReconnect = false, KillSwitch = false };
        await manager.ConnectAsync(Profile("tun"), tunSettings);
        Assert.Equal(ConnectionStatus.Connected, manager.Primary.Status);
        ports.Busy.Add(2080);
        var localSettings = new AppSettings { Mode = ConnectionMode.LocalProxy, LocalProxyPort = 2080, AutoReconnect = false };
        var issues = manager.PreviewConflicts(Profile("local"), localSettings);
        Assert.Contains(issues, i => i.Code == "port.local-proxy" && i.Severity == ConflictSeverity.Blocking);
    }

    private sealed class ReconnectableFakeEngine(IProcessRunner runner, EngineKind kind)
        : ProcessEngineBase(runner, new LogRedactor())
    {
        protected override TimeSpan ReadyTimeout => TimeSpan.Zero;
        public override EngineDescriptor Descriptor { get; } = new()
        {
            Kind = kind, DisplayName = "Fake", Upstream = "", License = "",
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
