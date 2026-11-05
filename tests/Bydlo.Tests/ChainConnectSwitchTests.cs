using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Import;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

/// <summary>Issue 333: chain connect button must switch even when another session is active.</summary>
public sealed class ChainConnectSwitchTests
{
    private static ImportedProfile Import(string text) => Assert.Single(new ProfileImporter().ImportText(text).Profiles);

    private static ConnectionManager Create() => new(
        new EngineRegistry(() => new MockEngine(seed: 1) { TimeScale = 0.01 }),
        new InMemorySecretStore(),
        new RootedAppPaths(TestDirs.New("chain-switch")),
        new ConflictDetector(new FakePorts()),
        new LogHub(new LogRedactor()),
        os: OsFamily.Windows);

    private static AppSettings MockSettings() => new() { MockMode = true };

    [Fact]
    public async Task Server_to_chain_switches_session()
    {
        await using var manager = Create();
        var single = Import("trojan://pw@one.example.com:443");
        await manager.ConnectAsync(single.Profile, MockSettings());
        Assert.Null(manager.Primary.Chain);

        var hop1 = Import("trojan://pw@two.example.com:443");
        var hop2 = Import("trojan://pw@three.example.com:443");
        var hops = new List<ChainHop> { new(hop1.Profile, hop1.Secrets), new(hop2.Profile, hop2.Secrets) };
        var chain = new ProxyChain { Name = "b", ProfileIds = [hop1.Profile.Id, hop2.Profile.Id] };

        await manager.ConnectChainAsync(chain, hops, MockSettings());

        Assert.Equal(ConnectionStatus.Connected, manager.Primary.Status);
        Assert.Equal(chain.Id, manager.Primary.Chain?.Id);
    }

    [Fact]
    public async Task Chain_to_chain_switches_session()
    {
        await using var manager = Create();
        var a1 = Import("trojan://pw@a1.example.com:443");
        var a2 = Import("trojan://pw@a2.example.com:443");
        var hopsA = new List<ChainHop> { new(a1.Profile, a1.Secrets), new(a2.Profile, a2.Secrets) };
        var chainA = new ProxyChain { Name = "a", ProfileIds = [a1.Profile.Id, a2.Profile.Id] };
        await manager.ConnectChainAsync(chainA, hopsA, MockSettings());

        var b1 = Import("trojan://pw@b1.example.com:443");
        var b2 = Import("trojan://pw@b2.example.com:443");
        var hopsB = new List<ChainHop> { new(b1.Profile, b1.Secrets), new(b2.Profile, b2.Secrets) };
        var chainB = new ProxyChain { Name = "b", ProfileIds = [b1.Profile.Id, b2.Profile.Id] };
        await manager.ConnectChainAsync(chainB, hopsB, MockSettings());

        Assert.Equal(ConnectionStatus.Connected, manager.Primary.Status);
        Assert.Equal(chainB.Id, manager.Primary.Chain?.Id);
    }

    [Fact]
    public void ConnectChain_calls_connect_chain_async_unconditionally()
    {
        var root = FindRepoRoot();
        var text = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "ViewModels", "ConnectionsViewModel.Chains.cs"));
        var start = text.IndexOf("private async Task ConnectChain(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = text.IndexOf("private async Task ToggleChainFavorite", StringComparison.Ordinal);
        Assert.True(end > start);
        var body = text.Substring(start, end - start);
        Assert.Contains("ConnectChainAsync", body);
        Assert.DoesNotContain("if (!Shell.Home.IsConnected)", body);
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
}
