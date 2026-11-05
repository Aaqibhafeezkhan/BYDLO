using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

/// <summary>Issue 332: Connect from the list must not drop the working session before validation.</summary>
public sealed class ListConnectTests
{
    private static Profile Compatible() => new()
    {
        Name = "a", Protocol = ProtocolKind.Trojan, Host = "a.invalid", Port = 443,
    };

    private static Profile IncompatibleXhttp() => new()
    {
        Name = "b",
        Protocol = ProtocolKind.Vless,
        Host = "b.invalid",
        Port = 443,
        Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Keys.Transport] = "xhttp",
            [Keys.Path] = "/x",
        },
    };

    private static ConnectionManager Create(EngineRegistry registry) =>
        new(registry, new InMemorySecretStore(), new RootedAppPaths(TestDirs.New("list-connect")),
            new ConflictDetector(new FakePorts()), new LogHub(new LogRedactor()), new NoProxy(), OsFamily.Windows);

    [Fact]
    public async Task Incompatible_list_switch_keeps_working_session()
    {
        var runner = new FakeProcessRunner();
        var created = 0;
        var registry = new EngineRegistry(() => throw new InvalidOperationException())
            .Register(new ListEngine(runner).Descriptor, () => { created++; return new ListEngine(runner); });
        await using var manager = Create(registry);
        await manager.ConnectAsync(Compatible(), new AppSettings());
        var before = manager.Primary;
        var process = runner.Last!;
        var settings = new AppSettings { Routing = { Preset = RoutingPreset.BlockedOnly } };

        var error = await Assert.ThrowsAsync<EngineException>(() => manager.ConnectAsync(IncompatibleXhttp(), settings));

        Assert.Equal("routing.preset.singbox", error.Message);
        Assert.Equal(before, manager.Primary);
        Assert.Equal(ConnectionStatus.Connected, manager.Primary.Status);
        Assert.False(process.Stopped);
        Assert.Equal(1, created);
    }

    [Fact]
    public async Task Compatible_list_switch_replaces_session()
    {
        var runner = new FakeProcessRunner();
        var registry = new EngineRegistry(() => throw new InvalidOperationException())
            .Register(new ListEngine(runner).Descriptor, () => new ListEngine(runner));
        await using var manager = Create(registry);
        await manager.ConnectAsync(Compatible(), new AppSettings());
        var process = runner.Last!;

        await manager.ConnectAsync(new Profile { Name = "c", Protocol = ProtocolKind.Trojan, Host = "c.invalid", Port = 443 }, new AppSettings());

        Assert.Equal(ConnectionStatus.Connected, manager.Primary.Status);
        Assert.True(process.Stopped);
        Assert.Equal("c", manager.Primary.Profile?.Name);
    }

    [Fact]
    public void ConnectTo_does_not_disconnect_before_connect()
    {
        var root = FindRepoRoot();
        var text = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "ViewModels", "ConnectionsViewModel.cs"));
        var start = text.IndexOf("private async Task ConnectTo(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = text.IndexOf("private async Task ToggleFavorite", StringComparison.Ordinal);
        Assert.True(end > start);
        var body = text.Substring(start, end - start);
        Assert.DoesNotContain("DisconnectAsync", body);
        Assert.Contains("ConnectAsync", body);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BYDLO.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private sealed class NoProxy : ISystemProxyService
    {
        public bool IsSupported => false;
        public Task SetAsync(string host, int port, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ListEngine(FakeProcessRunner runner) : ProcessEngineBase(runner, new LogRedactor())
    {
        protected override TimeSpan ReadyTimeout => TimeSpan.Zero;
        public override EngineDescriptor Descriptor { get; } = new()
        {
            Kind = EngineKind.Xray,
            DisplayName = "Fake",
            Upstream = "",
            License = "",
            Protocols = new HashSet<ProtocolKind> { ProtocolKind.Trojan, ProtocolKind.Vless },
            Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented },
        };
        public override bool CanHandle(Profile profile) => true;
        public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) =>
            Task.FromResult(EngineAvailability.Ready("fake"));
        protected override Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct) =>
            Task.FromResult(new ProcessSpec { FileName = "fake" });
    }
}
