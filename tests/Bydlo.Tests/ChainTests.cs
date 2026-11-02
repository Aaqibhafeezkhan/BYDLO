using System.Text.Json.Nodes;
using Bydlo.Core.Backup;
using Bydlo.Core.Config;
using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Import;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;
using Bydlo.Engines.SingBox;

namespace Bydlo.Tests;

/// <summary>Relay chain (issue #75) unit tests. No network, no processes, fake secrets only.</summary>
public class ChainTests
{
    private static ImportedProfile Import(string text) => Assert.Single(new ProfileImporter().ImportText(text).Profiles);

    private static ChainHop Hop(string text)
    {
        var p = Import(text);
        return new ChainHop(p.Profile, p.Secrets);
    }

    private static ProxyChain Chain(params Guid[] ids) =>
        new() { Name = "Test chain", ProfileIds = [.. ids] };

    private static ProfileStore NewStore()
    {
        var store = new ProfileStore(Path.Combine(TestDirs.New("chains"), "profiles.json"), new InMemorySecretStore());
        return store;
    }

    [Fact]
    public void Validator_rejects_too_few_unknown_and_duplicate_hops()
    {
        var a = Guid.NewGuid();
        var missing = Guid.NewGuid();
        Profile? Lookup(Guid id) => id == a ? new Profile { Id = a, Name = "a", Protocol = ProtocolKind.Vless } : null;

        Assert.Contains("error.chain.toofew", ChainValidator.Validate(Chain(a), Lookup));
        Assert.Contains("error.chain.unknown", ChainValidator.Validate(Chain(a, missing), Lookup));
        Assert.Contains("error.chain.duplicate", ChainValidator.Validate(Chain(a, a), Lookup));
        Assert.Throws<EngineException>(() => ChainValidator.ThrowIfInvalid(Chain(a), Lookup));
    }

    [Fact]
    public void Validator_rejects_non_chainable_preferred_engine()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Profile? Lookup(Guid id) => new Profile { Id = id, Name = "x", Protocol = ProtocolKind.Vless };
        var chain = Chain(a, b);
        chain.PreferredEngine = EngineKind.OpenVpn;
        Assert.Contains("error.chain.engine", ChainValidator.Validate(chain, Lookup));
        chain.PreferredEngine = EngineKind.SingBox;
        Assert.Empty(ChainValidator.Validate(chain, Lookup));
    }

    [Fact]
    public async Task Store_add_update_remove_chain_and_detach_deleted_profiles()
    {
        var store = NewStore();
        await store.LoadAsync();
        var added = await store.AddAsync([Import("vless://11111111-2222-3333-4444-555555555555@one.example.com:443"), Import("trojan://pw@two.example.com:443")]);
        var chain = await store.AddChainAsync("EU relay", [added[0].Id, added[1].Id]);
        Assert.Single(store.Chains);

        await Assert.ThrowsAsync<EngineException>(() => store.AddChainAsync("bad", [added[0].Id]));
        await Assert.ThrowsAsync<EngineException>(() => store.AddChainAsync("dup", [added[0].Id, added[0].Id]));

        chain.Name = "Renamed";
        await store.UpdateChainAsync(chain);
        Assert.Equal("Renamed", store.Chains[0].Name);

        await store.RemoveAsync(added[0].Id);
        Assert.Empty(store.Chains);

        // Detach drops the deleted hop; a chain left with one hop is dropped too.
        var more = await store.AddAsync([Import("vless://22222222-3333-4444-5555-666666666666@three.example.com:443")]);
        await store.AddChainAsync("Pair", [added[1].Id, more[0].Id]);
        Assert.Single(store.Chains);
        await store.RemoveAsync(more[0].Id);
        Assert.Empty(store.Chains);
    }

    [Fact]
    public async Task Store_rejects_chain_with_unknown_profile()
    {
        var store = NewStore();
        await store.LoadAsync();
        await Assert.ThrowsAsync<EngineException>(() => store.AddChainAsync("ghost", [Guid.NewGuid(), Guid.NewGuid()]));
    }

    [Fact]
    public void Builders_reject_too_many_hops()
    {
        var hops = Enumerable.Range(0, 6)
            .Select(i => Hop($"trojan://pw@host{i}.example.com:443"))
            .ToList();
        Assert.Equal("error.chain.toofew", Assert.Throws<EngineException>(() =>
            new SingBoxConfigBuilder().BuildChain([hops[0]], new BuildContext(new AppSettings()))).LocalizationKey);
        Assert.Equal("error.chain.toolong", Assert.Throws<EngineException>(() =>
            new SingBoxConfigBuilder().BuildChain(hops, new BuildContext(new AppSettings()))).LocalizationKey);
        Assert.Equal("error.chain.toolong", Assert.Throws<EngineException>(() =>
            new XrayConfigBuilder().BuildChain(hops, new BuildContext(new AppSettings()))).LocalizationKey);
    }

    [Fact]
    public async Task Store_rejects_unchainable_hops()
    {
        var store = NewStore();
        await store.LoadAsync();
        var ovpnA = new Profile { Name = "a", Protocol = ProtocolKind.OpenVpn, Host = "a.example.com", Port = 1194 };
        var ovpnB = new Profile { Name = "b", Protocol = ProtocolKind.OpenVpn, Host = "b.example.com", Port = 1194 };
        await store.AddAsync([new(ovpnA, ProfileSecrets.Empty), new(ovpnB, ProfileSecrets.Empty)]);
        var ex = await Assert.ThrowsAsync<EngineException>(() => store.AddChainAsync("Nope", [ovpnA.Id, ovpnB.Id]));
        Assert.Equal("error.chain.engine", ex.LocalizationKey);
    }

    [Fact]
    public async Task SingBox_engine_writes_chain_config()
    {
        var runner = new FakeProcessRunner();
        runner.StartupOutput.Add("sing-box started");
        var engine = new SingBoxEngine(runner, new LogRedactor(),
            new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64)), new FakePrivileges(true));
        var hops = new List<ChainHop> { Hop("trojan://pw@one.example.com:443"), Hop("trojan://pw@two.example.com:443") };
        var workdir = TestDirs.New("chain-engine");
        await engine.StartAsync(new EngineStartRequest
        {
            Profile = hops[^1].Profile,
            Secrets = hops[^1].Secrets,
            Chain = hops,
            Settings = new AppSettings(),
            WorkDirectory = workdir,
        });
        try
        {
            var json = await File.ReadAllTextAsync(Path.Combine(workdir, "sing-box.json"));
            Assert.Contains("proxy-1", json, StringComparison.Ordinal);
            Assert.Contains("detour", json, StringComparison.Ordinal);
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    [Fact]
    public async Task PreviewChainConflicts_dedupes_shared_issues()
    {
        await using var manager = NewManager(new FakePorts(2080));
        var hops = new List<Profile> { Hop("trojan://pw@one.example.com:443").Profile, Hop("trojan://pw@two.example.com:443").Profile };
        var issues = manager.PreviewChainConflicts(hops, new AppSettings());
        Assert.Single(issues, i => i.Code == "port.local-proxy");
    }

    private static ConnectionManager NewManager(FakePorts? ports = null) => new(
        new EngineRegistry(() => new MockEngine()),
        new InMemorySecretStore(),
        new RootedAppPaths(TestDirs.New("cm-chain-x")),
        new ConflictDetector(ports ?? new FakePorts()),
        new LogHub(new LogRedactor()),
        os: OsFamily.Windows);

    [Fact]
    public void SingBox_chain_uses_detour_between_hops()
    {
        var hops = new List<ChainHop> { Hop("vless://11111111-2222-3333-4444-555555555555@one.example.com:443"), Hop("trojan://pw@two.example.com:443") };
        var root = new SingBoxConfigBuilder().BuildChain(hops, new BuildContext(new AppSettings()));
        var tags = root["outbounds"]!.AsArray().Select(o => (string?)o!["tag"]).ToList();
        Assert.Equal(["proxy-0", "proxy-1", "direct"], tags);
        Assert.Null(root["outbounds"]![0]!["detour"]);
        Assert.Equal("proxy-0", (string?)root["outbounds"]![1]!["detour"]);
        Assert.Equal("proxy-1", (string?)root["route"]!["final"]);
        Assert.Equal("proxy-1", (string?)root["dns"]!["servers"]![0]!["detour"]);
    }

    [Fact]
    public void SingBox_chain_supports_wireguard_hop_as_endpoint()
    {
        var conf = "[Interface]\nPrivateKey = yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=\nAddress = 10.8.0.2/32\n\n[Peer]\nPublicKey = xTIBA5rboUvnH4htodjb6e697QjLERt1NAB4mZqp8Dg=\nAllowedIPs = 0.0.0.0/0\nEndpoint = 203.0.113.5:51820\n";
        var hops = new List<ChainHop> { Hop("trojan://pw@one.example.com:443"), Hop(conf) };
        var root = new SingBoxConfigBuilder().BuildChain(hops, new BuildContext(new AppSettings()));
        Assert.Equal("proxy-0", (string?)root["endpoints"]![0]!["detour"]);
        Assert.Equal("proxy-1", (string?)root["route"]!["final"]);
    }

    [Fact]
    public void SingBox_chain_rejects_unsupported_hop()
    {
        var ovpn = new Profile { Name = "ovpn", Protocol = ProtocolKind.OpenVpn, Host = "vpn.example.com", Port = 1194 };
        var hops = new List<ChainHop>
        {
            Hop("trojan://pw@one.example.com:443"),
            new ChainHop(ovpn, ProfileSecrets.Empty),
        };
        var ex = Assert.Throws<EngineException>(() => new SingBoxConfigBuilder().BuildChain(hops, new BuildContext(new AppSettings())));
        Assert.Equal("error.chain.engine", ex.LocalizationKey);
    }

    [Fact]
    public void Xray_chain_uses_proxy_settings_between_hops()
    {
        var hops = new List<ChainHop>
        {
            Hop("vless://11111111-2222-3333-4444-555555555555@one.example.com:443?security=tls&sni=one.example.com"),
            Hop("trojan://pw@two.example.com:443"),
        };
        var root = new XrayConfigBuilder().BuildChain(hops, new BuildContext(new AppSettings()));
        var tags = root["outbounds"]!.AsArray().Select(o => (string?)o!["tag"]).ToList();
        Assert.Equal(["proxy-0", "proxy-1", "direct", "block"], tags);
        Assert.Null(root["outbounds"]![0]!["proxySettings"]);
        Assert.Equal("proxy-0", (string?)root["outbounds"]![1]!["proxySettings"]!["tag"]);
        Assert.Equal("proxy-1", (string?)root["routing"]!["rules"]![0]!["outboundTag"]);
    }

    [Fact]
    public void Registry_picks_chain_capable_engine_for_all_hops()
    {
        var registry = new EngineRegistry(() => new MockEngine())
            .Register(new EngineDescriptor { Kind = EngineKind.SingBox, DisplayName = "sb", Upstream = "", License = "", Protocols = SingBoxConfigBuilder.Supported, Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented } }, () => new MockEngine(), 10)
            .Register(new EngineDescriptor { Kind = EngineKind.Xray, DisplayName = "x", Upstream = "", License = "", Protocols = XrayConfigBuilder.Supported, Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented } }, () => new MockEngine(), 20);
        var hops = new List<Profile> { Hop("trojan://pw@one.example.com:443").Profile, Hop("trojan://pw@two.example.com:443").Profile };
        Assert.Equal(EngineKind.SingBox, registry.CandidatesForChain(hops, OsFamily.Windows)[0]);

        var xhttp = Hop("vless://id@x.example.com:443?security=tls&type=xhttp&path=%2Fx&mode=auto&sni=x.example.com").Profile;
        Assert.Equal([EngineKind.Xray], registry.CandidatesForChain([hops[0], xhttp], OsFamily.Windows));

        var wgConf = "[Interface]\nPrivateKey = yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=\nAddress = 10.8.0.2/32\n\n[Peer]\nPublicKey = xTIBA5rboUvnH4htodjb6e697QjLERt1NAB4mZqp8Dg=\nAllowedIPs = 0.0.0.0/0\nEndpoint = 203.0.113.5:51820\n";
        var wg = Hop(wgConf).Profile;
        Assert.Equal([EngineKind.SingBox], registry.CandidatesForChain([hops[0], wg], OsFamily.Windows));
    }

    [Fact]
    public async Task ConnectionManager_connects_chain_in_mock_mode()
    {
        await using var manager = new ConnectionManager(
            new EngineRegistry(() => new MockEngine(seed: 1) { TimeScale = 0.01 }),
            new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("cm-chain")),
            new ConflictDetector(new FakePorts()),
            new LogHub(new LogRedactor()),
            os: OsFamily.Windows);
        var hops = new List<ChainHop> { Hop("trojan://pw@one.example.com:443"), Hop("trojan://pw@two.example.com:443") };
        var chain = Chain(hops[0].Profile.Id, hops[1].Profile.Id);
        await manager.ConnectChainAsync(chain, hops, new AppSettings { MockMode = true });
        Assert.Equal(ConnectionStatus.Connected, manager.Primary.Status);
        Assert.Equal(chain.Id, manager.Primary.Chain?.Id);
        Assert.Equal(hops[1].Profile.Id, manager.Primary.Profile?.Id);
        await manager.DisconnectAsync();
    }

    [Fact]
    public async Task ConnectionManager_rejects_mismatched_chain_hops()
    {
        await using var manager = new ConnectionManager(
            new EngineRegistry(() => new MockEngine()),
            new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("cm-chain-bad")),
            new ConflictDetector(new FakePorts()),
            new LogHub(new LogRedactor()),
            os: OsFamily.Windows);
        var hops = new List<ChainHop> { Hop("trojan://pw@one.example.com:443") };
        await Assert.ThrowsAsync<EngineException>(() =>
            manager.ConnectChainAsync(Chain(hops[0].Profile.Id, Guid.NewGuid()), hops, new AppSettings { MockMode = true }));
    }

    [Theory]
    [InlineData(BackupImportMode.Merge)]
    [InlineData(BackupImportMode.Replace)]
    public async Task Backup_roundtrips_chains_and_drops_stale_ones(BackupImportMode mode)
    {
        var folder = TestDirs.New("backup-chains");
        var store = NewStoreAt(folder);
        await store.LoadAsync();
        var added = await store.AddAsync([Import("trojan://pw@one.example.com:443"), Import("trojan://pw@two.example.com:443")]);
        await store.AddChainAsync("Relay", [added[0].Id, added[1].Id]);

        var snapshot = await store.CaptureBackupAsync(new AppSettings());
        Assert.Single(snapshot.Chains);
        snapshot.Chains.Add(new ProxyChain { Name = "Stale", ProfileIds = [added[0].Id, Guid.NewGuid()] });

        var target = NewStoreAt(TestDirs.New("backup-chains-target"));
        await target.LoadAsync();
        await target.RestoreBackupAsync(snapshot, mode);
        var chain = Assert.Single(target.Chains);
        Assert.Equal("Relay", chain.Name);
        Assert.Equal([added[0].Id, added[1].Id], chain.ProfileIds);
    }

    private static ProfileStore NewStoreAt(string folder) =>
        new(Path.Combine(folder, "profiles.json"), new InMemorySecretStore());
}
