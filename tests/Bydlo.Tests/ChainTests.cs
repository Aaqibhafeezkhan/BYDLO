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

    [Theory]
    [InlineData(EngineKind.Xray, ConnectionMode.LocalProxy, false, "routing.preset.singbox")]
    [InlineData(EngineKind.SingBox, ConnectionMode.Tun, true, "routing.preset.killswitch")]
    public void Explicit_chain_engine_respects_routing_restrictions(EngineKind kind, ConnectionMode mode, bool killSwitch, string key)
    {
        var registry = new EngineRegistry(() => new MockEngine())
            .Register(new EngineDescriptor
            {
                Kind = kind, DisplayName = "fake", Upstream = "", License = "",
                Protocols = new HashSet<ProtocolKind> { ProtocolKind.Trojan },
                Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented },
            }, () => throw new InvalidOperationException("An incompatible engine must not be created."));
        var profile = Hop("trojan://pw@one.example.com:443").Profile;
        var settings = new AppSettings { Mode = mode, KillSwitch = killSwitch, Routing = { Preset = RoutingPreset.BlockedOnly } };
        var error = Assert.Throws<EngineException>(() => registry.CreateForChain([profile, profile], kind, settings, OsFamily.Windows));
        Assert.Equal(key, error.LocalizationKey);
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

    private static async Task<ProfileStore> StoreWithChainAsync(string folder, bool rich = false)
    {
        var store = new ProfileStore(Path.Combine(folder, "profiles.json"), new InMemorySecretStore());
        await store.LoadAsync();
        var added = await store.AddAsync([Import("trojan://pw@one.example.com:443"), Import("trojan://pw@two.example.com:443")]);
        await store.AddChainAsync("Relay", [added[0].Id, added[1].Id]);
        if (rich)
        {
            var chain = Assert.Single(store.Chains);
            await store.UpdateChainAsync(new ProxyChain
            {
                Id = chain.Id, Name = chain.Name, ProfileIds = [.. chain.ProfileIds],
                PreferredEngine = EngineKind.SingBox, AddedAt = chain.AddedAt, IsFavorite = true,
            });
        }
        return store;
    }

    private static Blocker BreakWrites(string file)
    {
        Directory.CreateDirectory(file + ".tmp");
        return new Blocker(file);
    }

    private sealed class Blocker(string file) : IDisposable
    {
        public void Dispose()
        {
            if (Directory.Exists(file + ".tmp")) { Directory.Delete(file + ".tmp"); }
        }
    }

    private static async Task<Exception?> RecordWriteErrorAsync(Func<Task> action)
    {
        var error = await Record.ExceptionAsync(action);
        Assert.True(error is IOException or UnauthorizedAccessException, $"Expected a storage failure, got {error?.GetType().Name ?? "none"}.");
        return error;
    }

    private static async Task AssertMemoryEqualsDiskAsync(ProfileStore store, string file)
    {
        var reloaded = new ProfileStore(file, new InMemorySecretStore());
        await reloaded.LoadAsync();
        var expected = store.Chains.OrderBy(c => c.Id).ToList();
        var actual = reloaded.Chains.OrderBy(c => c.Id).ToList();
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Id, actual[i].Id);
            Assert.Equal(expected[i].Name, actual[i].Name);
            Assert.Equal(expected[i].ProfileIds, actual[i].ProfileIds);
            Assert.Equal(expected[i].PreferredEngine, actual[i].PreferredEngine);
            Assert.Equal(expected[i].IsFavorite, actual[i].IsFavorite);
            Assert.Equal(expected[i].AddedAt, actual[i].AddedAt);
        }
    }

    private static void AssertChainFields(ProxyChain chain, string name, IReadOnlyList<Guid> hops,
        EngineKind? engine = null, bool favorite = false)
    {
        Assert.Equal(name, chain.Name);
        Assert.Equal(hops, chain.ProfileIds);
        Assert.Equal(engine, chain.PreferredEngine);
        Assert.Equal(favorite, chain.IsFavorite);
    }

    [Fact]
    public async Task Failed_update_keeps_memory_equal_to_disk()
    {
        var folder = TestDirs.New("chain-update-failure");
        var store = await StoreWithChainAsync(folder, rich: true);
        var file = Path.Combine(folder, "profiles.json");
        var before = await File.ReadAllTextAsync(file);
        var original = Assert.Single(store.Chains);
        using var _ = BreakWrites(file);

        // A different but valid hop order must not leak into memory when the write fails.
        var reversed = new ProxyChain
        {
            Id = original.Id, Name = "Renamed", ProfileIds = [.. Enumerable.Reverse(original.ProfileIds)],
            PreferredEngine = EngineKind.Xray, AddedAt = original.AddedAt, IsFavorite = false,
        };
        await RecordWriteErrorAsync(() => store.UpdateChainAsync(reversed));

        Assert.Equal(before, await File.ReadAllTextAsync(file));
        AssertChainFields(Assert.Single(store.Chains), "Relay", original.ProfileIds, EngineKind.SingBox, favorite: true);
        await AssertMemoryEqualsDiskAsync(store, file);
    }

    [Fact]
    public async Task Failed_rename_keeps_memory_equal_to_disk()
    {
        var folder = TestDirs.New("chain-rename-failure");
        var store = await StoreWithChainAsync(folder, rich: true);
        var file = Path.Combine(folder, "profiles.json");
        var before = await File.ReadAllTextAsync(file);
        var original = Assert.Single(store.Chains);
        using var _ = BreakWrites(file);

        await RecordWriteErrorAsync(() => store.RenameChainAsync(original.Id, "Renamed"));

        Assert.Equal(before, await File.ReadAllTextAsync(file));
        AssertChainFields(Assert.Single(store.Chains), "Relay", original.ProfileIds, EngineKind.SingBox, favorite: true);
        await AssertMemoryEqualsDiskAsync(store, file);
    }

    [Fact]
    public async Task Failed_remove_keeps_memory_equal_to_disk()
    {
        var folder = TestDirs.New("chain-remove-failure");
        var store = await StoreWithChainAsync(folder, rich: true);
        var file = Path.Combine(folder, "profiles.json");
        var before = await File.ReadAllTextAsync(file);
        var original = Assert.Single(store.Chains);
        using var _ = BreakWrites(file);

        await RecordWriteErrorAsync(() => store.RemoveChainAsync(original.Id));

        Assert.Equal(before, await File.ReadAllTextAsync(file));
        AssertChainFields(Assert.Single(store.Chains), "Relay", original.ProfileIds, EngineKind.SingBox, favorite: true);
        await AssertMemoryEqualsDiskAsync(store, file);
    }

    [Fact]
    public async Task Rejected_update_validation_keeps_memory_equal_to_disk()
    {
        // Validation throws before any write: memory and disk must be untouched (no write attempted).
        var folder = TestDirs.New("chain-update-validation");
        var store = await StoreWithChainAsync(folder, rich: true);
        var file = Path.Combine(folder, "profiles.json");
        var before = await File.ReadAllTextAsync(file);
        var original = Assert.Single(store.Chains);

        var duplicate = new ProxyChain { Id = original.Id, Name = "Bad", ProfileIds = [original.ProfileIds[0], original.ProfileIds[0]] };
        var error = await Assert.ThrowsAsync<EngineException>(() => store.UpdateChainAsync(duplicate));
        Assert.Equal("error.chain.duplicate", error.LocalizationKey);

        Assert.Equal(before, await File.ReadAllTextAsync(file));
        AssertChainFields(Assert.Single(store.Chains), "Relay", original.ProfileIds, EngineKind.SingBox, favorite: true);
        await AssertMemoryEqualsDiskAsync(store, file);
    }

    [Fact]
    public async Task Cancelled_chain_mutation_keeps_memory_equal_to_disk()
    {
        var folder = TestDirs.New("chain-cancel");
        var store = await StoreWithChainAsync(folder);
        var file = Path.Combine(folder, "profiles.json");
        var before = await File.ReadAllTextAsync(file);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var id = store.Chains[0].Id;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RenameChainAsync(id, "Renamed", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.UpdateChainAsync(new ProxyChain { Id = id, Name = "Renamed", ProfileIds = [.. store.Chains[0].ProfileIds] }, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RemoveChainAsync(id, cts.Token));

        Assert.Equal(before, await File.ReadAllTextAsync(file));
        Assert.Equal("Relay", Assert.Single(store.Chains).Name);
        await AssertMemoryEqualsDiskAsync(store, file);
    }

    [Fact]
    public async Task Successful_retry_after_failed_chain_write_persists()
    {
        var folder = TestDirs.New("chain-retry");
        var store = await StoreWithChainAsync(folder, rich: true);
        var file = Path.Combine(folder, "profiles.json");
        var before = await File.ReadAllTextAsync(file);
        var original = Assert.Single(store.Chains);
        var blocker = BreakWrites(file);
        var id = original.Id;
        await RecordWriteErrorAsync(() => store.RenameChainAsync(id, "Renamed"));

        // The failed write changed nothing: memory and disk still hold the original chain.
        Assert.Equal(before, await File.ReadAllTextAsync(file));
        AssertChainFields(Assert.Single(store.Chains), "Relay", original.ProfileIds, EngineKind.SingBox, favorite: true);
        blocker.Dispose();

        await store.RenameChainAsync(id, "Renamed");
        AssertChainFields(Assert.Single(store.Chains), "Renamed", original.ProfileIds, EngineKind.SingBox, favorite: true);
        var reloaded = new ProfileStore(file, new InMemorySecretStore());
        await reloaded.LoadAsync();
        AssertChainFields(Assert.Single(reloaded.Chains), "Renamed", original.ProfileIds, EngineKind.SingBox, favorite: true);
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
    public void SingBox_wireguard_reserved_goes_inside_peers()
    {
        var imported = Hop("trojan://pw@one.example.com:443");
        var conf = "[Interface]\nPrivateKey = yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=\nAddress = 10.8.0.2/32\n\n[Peer]\nPublicKey = xTIBA5rboUvnH4htodjb6e697QjLERt1NAB4mZqp8Dg=\nAllowedIPs = 0.0.0.0/0\nEndpoint = 203.0.113.5:51820\n";
        var wg = Import(conf);
        wg.Profile.Options[Keys.Reserved] = "1,2,3";
        var root = new SingBoxConfigBuilder().BuildChain([imported, new ChainHop(wg.Profile, wg.Secrets)], new BuildContext(new AppSettings()));
        var ep = root["endpoints"]![0]!;
        Assert.Null(ep["reserved"]);
        Assert.Equal([1, 2, 3], ep["peers"]![0]!["reserved"]!.AsArray().Select(n => (int)n!));
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

    private static ImportedProfile SingBoxJsonHop() => Assert.Single(new ProfileImporter().ImportText(
        """{"inbounds":[{"type":"mixed","listen":"0.0.0.0","listen_port":2222}],"outbounds":[{"type":"trojan","tag":"remote","server":"proxy.invalid","server_port":443,"password":"hidden"}]}""").Profiles);

    private static ImportedProfile XrayJsonHop() => Assert.Single(new ProfileImporter().ImportText(
        """{"inbounds":[{"protocol":"http","listen":"0.0.0.0","port":2222}],"outbounds":[{"protocol":"trojan","tag":"remote","settings":{"servers":[{"address":"proxy.invalid","port":443,"password":"hidden"}]}}]}""").Profiles);

    [Theory]
    [InlineData(ProtocolKind.SingBoxConfig)]
    [InlineData(ProtocolKind.XrayConfig)]
    public void Builders_reject_raw_json_hops_with_chain_engine_error(ProtocolKind json)
    {
        var raw = json == ProtocolKind.SingBoxConfig ? SingBoxJsonHop() : XrayJsonHop();
        var hops = new List<ChainHop> { Hop("trojan://pw@one.example.com:443"), new ChainHop(raw.Profile, raw.Secrets) };
        var singBoxError = Assert.Throws<EngineException>(() =>
            new SingBoxConfigBuilder().BuildChain(hops, new BuildContext(new AppSettings())));
        Assert.Equal("error.chain.engine", singBoxError.LocalizationKey);
        var xrayError = Assert.Throws<EngineException>(() =>
            new XrayConfigBuilder().BuildChain(hops, new BuildContext(new AppSettings())));
        Assert.Equal("error.chain.engine", xrayError.LocalizationKey);
    }

    [Theory]
    [InlineData(ProtocolKind.SingBoxConfig)]
    [InlineData(ProtocolKind.XrayConfig)]
    public async Task Store_refuses_to_save_chain_with_raw_json_hop(ProtocolKind json)
    {
        var store = NewStore();
        await store.LoadAsync();
        var raw = json == ProtocolKind.SingBoxConfig ? SingBoxJsonHop() : XrayJsonHop();
        var plain = Import("trojan://pw@two.example.com:443");
        await store.AddAsync([new ImportedProfile(raw.Profile, raw.Secrets), plain]);
        var ex = await Assert.ThrowsAsync<EngineException>(() => store.AddChainAsync("Json relay", [raw.Profile.Id, plain.Profile.Id]));
        Assert.Equal("error.chain.engine", ex.LocalizationKey);
        Assert.Empty(store.Chains);
    }

    [Theory]
    [InlineData(ProtocolKind.SingBoxConfig)]
    [InlineData(ProtocolKind.XrayConfig)]
    public void Registry_reports_no_chain_engine_for_raw_json_hops(ProtocolKind json)
    {
        var registry = new EngineRegistry(() => new MockEngine())
            .Register(new EngineDescriptor { Kind = EngineKind.SingBox, DisplayName = "sb", Upstream = "", License = "", Protocols = SingBoxConfigBuilder.Supported, Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented } }, () => new MockEngine(), 10)
            .Register(new EngineDescriptor { Kind = EngineKind.Xray, DisplayName = "x", Upstream = "", License = "", Protocols = XrayConfigBuilder.Supported, Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented } }, () => new MockEngine(), 20);
        var raw = (json == ProtocolKind.SingBoxConfig ? SingBoxJsonHop() : XrayJsonHop()).Profile;
        var plain = Hop("trojan://pw@two.example.com:443").Profile;
        Assert.Empty(registry.CandidatesForChain([raw, plain], OsFamily.Windows));
        var error = Assert.Throws<EngineException>(() =>
            registry.CreateForChain([raw, plain], null, new AppSettings(), OsFamily.Windows));
        Assert.Equal("error.chain.engine", error.LocalizationKey);
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
        // The exit hop goes first: Xray sends unmatched traffic through the first outbound,
        // so the default must carry the whole chain, not just the entry hop.
        Assert.Equal(["proxy-1", "proxy-0", "direct", "block"], tags);
        var exit = root["outbounds"]![0]!;
        var entry = root["outbounds"]![1]!;
        Assert.Null(entry["proxySettings"]);
        Assert.Equal("proxy-0", (string?)exit["proxySettings"]!["tag"]);
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

    [Fact]
    public async Task Store_update_rejects_raw_json_hop_and_keeps_saved_chain()
    {
        var store = NewStore();
        await store.LoadAsync();
        var first = Import("trojan://pw@one.example.com:443");
        var second = Import("trojan://pw@two.example.com:443");
        var raw = SingBoxJsonHop();
        await store.AddAsync([first, second, new ImportedProfile(raw.Profile, raw.Secrets)]);
        var chain = await store.AddChainAsync("Relay", [first.Profile.Id, second.Profile.Id]);

        var updated = new ProxyChain { Id = chain.Id, Name = "Relay", ProfileIds = [first.Profile.Id, raw.Profile.Id] };
        var ex = await Assert.ThrowsAsync<EngineException>(() => store.UpdateChainAsync(updated));
        Assert.Equal("error.chain.engine", ex.LocalizationKey);
        Assert.Equal([first.Profile.Id, second.Profile.Id], Assert.Single(store.Chains).ProfileIds);
    }

    [Fact]
    public async Task Connect_reports_chain_engine_error_for_legacy_json_chain()
    {
        var registry = new EngineRegistry(() => new MockEngine())
            .Register(new EngineDescriptor { Kind = EngineKind.SingBox, DisplayName = "sb", Upstream = "", License = "", Protocols = SingBoxConfigBuilder.Supported, Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented } }, () => new MockEngine(), 10)
            .Register(new EngineDescriptor { Kind = EngineKind.Xray, DisplayName = "x", Upstream = "", License = "", Protocols = XrayConfigBuilder.Supported, Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented } }, () => new MockEngine(), 20);
        await using var manager = new ConnectionManager(
            registry,
            new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("cm-chain-json")),
            new ConflictDetector(new FakePorts()),
            new LogHub(new LogRedactor()),
            os: OsFamily.Windows);
        // A chain saved before the fix (or restored from an old backup) has no save-time
        // rejection to rely on: connecting must fail with a clear error, not NotSupportedException.
        var raw = SingBoxJsonHop();
        var plain = Hop("trojan://pw@two.example.com:443");
        var chain = new ProxyChain { Name = "Legacy", ProfileIds = [raw.Profile.Id, plain.Profile.Id] };
        var hops = new List<ChainHop> { new(raw.Profile, raw.Secrets), plain };
        var error = await Assert.ThrowsAsync<EngineException>(() =>
            manager.ConnectChainAsync(chain, hops, new AppSettings { MockMode = false }));
        Assert.Equal("error.chain.engine", error.LocalizationKey);
    }

    [Theory]
    [InlineData(BackupImportMode.Merge)]
    [InlineData(BackupImportMode.Replace)]
    public async Task Backup_restore_drops_chains_with_raw_json_hops(BackupImportMode mode)
    {
        var folder = TestDirs.New("backup-json-chains");
        var store = NewStoreAt(folder);
        await store.LoadAsync();
        var plain = Import("trojan://pw@one.example.com:443");
        var raw = SingBoxJsonHop();
        await store.AddAsync([plain, new ImportedProfile(raw.Profile, raw.Secrets)]);

        var snapshot = await store.CaptureBackupAsync(new AppSettings());
        snapshot.Chains.Add(new ProxyChain { Name = "Json", ProfileIds = [plain.Profile.Id, raw.Profile.Id] });

        var target = NewStoreAt(TestDirs.New("backup-json-chains-target"));
        await target.LoadAsync();
        await target.RestoreBackupAsync(snapshot, mode);
        Assert.Empty(target.Chains);
        Assert.Equal(2, target.Profiles.Count);
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
