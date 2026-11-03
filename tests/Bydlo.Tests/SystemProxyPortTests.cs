using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

public sealed class SystemProxyPortTests
{
    [Theory]
    [InlineData(OsFamily.Linux)]
    [InlineData(OsFamily.MacOS)]
    public async Task Mock_mode_never_sets_or_clears_the_desktop_proxy(OsFamily os)
    {
        var registry = new EngineRegistry(() => new MockEngine(seed: 1) { TimeScale = 0.01 });
        var proxy = new RecordingProxy();
        await using var manager = new ConnectionManager(registry, new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("mock-system-proxy")), new ConflictDetector(new FakePorts()),
            new LogHub(new LogRedactor()), proxy, os)
        {
            EnsureSystemProxyAllowed = () => throw new InvalidOperationException("Mock Mode must skip native proxy setup."),
        };
        await manager.ConnectAsync(new Profile { Protocol = ProtocolKind.Trojan, Host = "mock.invalid", Port = 443 },
            new AppSettings { MockMode = true, SetSystemProxy = true, AutoReconnect = false });
        Assert.True(manager.Primary.IsMock);
        Assert.Equal(ConnectionStatus.Connected, manager.Primary.Status);
        await manager.DisconnectAsync();
        Assert.Empty(proxy.Applied);
        Assert.Equal(0, proxy.Clears);
    }

    [Theory]
    [InlineData(EngineKind.Xray, false, 2346)]
    [InlineData(EngineKind.Xray, true, 2346)]
    [InlineData(EngineKind.SingBox, false, 2345)]
    [InlineData(EngineKind.SingBox, true, 2345)]
    public async Task System_proxy_uses_the_http_inbound(EngineKind kind, bool chain, int expectedPort)
    {
        var runner = new FakeProcessRunner();
        var engine = new RecordingEngine(runner, kind);
        var registry = new EngineRegistry(() => throw new InvalidOperationException())
            .Register(engine.Descriptor, () => engine);
        var proxy = new RecordingProxy();
        var safetyChecks = 0;
        await using var manager = new ConnectionManager(registry, new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("system-proxy-port")), new ConflictDetector(new FakePorts()),
            new LogHub(new LogRedactor()), proxy, OsFamily.Windows)
        {
            EnsureSystemProxyAllowed = () => safetyChecks++,
        };
        var settings = new AppSettings { LocalProxyPort = 2345, SetSystemProxy = true, AutoReconnect = false };
        var first = new Profile { Protocol = ProtocolKind.Trojan, Host = "first.invalid", Port = 443 };
        var last = new Profile { Protocol = ProtocolKind.Trojan, Host = "last.invalid", Port = 443 };
        if (chain)
        {
            var relay = new ProxyChain { Name = "test", ProfileIds = [first.Id, last.Id], PreferredEngine = kind };
            await manager.ConnectChainAsync(relay, [new(first, new()), new(last, new())], settings);
        }
        else
        {
            await manager.ConnectAsync(first, settings);
        }

        Assert.Equal(("127.0.0.1", expectedPort), Assert.Single(proxy.Applied));
        Assert.Equal(1, safetyChecks);
        Assert.Equal(ConnectionStatus.Connected, manager.Primary.Status);
        await manager.DisconnectAsync();
        Assert.Equal(1, proxy.Clears);
        Assert.True(runner.Last!.Stopped);
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

    private sealed class RecordingEngine(IProcessRunner runner, EngineKind kind)
        : ProcessEngineBase(runner, new LogRedactor())
    {
        protected override TimeSpan ReadyTimeout => TimeSpan.Zero;
        public override EngineDescriptor Descriptor { get; } = new()
        {
            Kind = kind, DisplayName = "Fake proxy engine", Upstream = "", License = "",
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
