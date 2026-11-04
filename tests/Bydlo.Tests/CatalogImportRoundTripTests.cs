using Bydlo.Core.Config;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Import;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

/// <summary>Issue #242: public servers break after import; ping immediately and convenient view.</summary>
public class CatalogImportRoundTripTests
{
    private static EngineRegistry Registry() =>
        new EngineRegistry(() => new MockEngine())
            .Register(new EngineDescriptor { Kind = EngineKind.SingBox, DisplayName = "sb", Upstream = "", License = "", Protocols = SingBoxConfigBuilder.Supported, Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Linux] = SupportLevel.Implemented } }, () => new MockEngine(), 10)
            .Register(new EngineDescriptor { Kind = EngineKind.Xray, DisplayName = "x", Upstream = "", License = "", Protocols = XrayConfigBuilder.Supported, Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Linux] = SupportLevel.Implemented } }, () => new MockEngine(), 20);

    private static Profile Base(ProtocolKind kind) =>
        new() { Protocol = kind, Host = "example.com", Port = 443 };

    [Fact]
    public void H2_transport_forces_singbox()
    {
        var p = Base(ProtocolKind.Vless);
        p.Options[Keys.Transport] = "h2";
        Assert.Equal([EngineKind.SingBox], Registry().CandidatesFor(p, OsFamily.Linux));
        var secrets = new ProfileSecrets { Values = { [Keys.Uuid] = Guid.NewGuid().ToString() } };
        var ex = Assert.Throws<EngineException>(() =>
            new XrayConfigBuilder().Build(p, secrets, new BuildContext(new AppSettings())));
        Assert.Equal("error.engine.transport.singbox", ex.LocalizationKey);
    }

    [Fact]
    public void Https_proxy_forces_singbox_and_xray_refuses_silent_downgrade()
    {
        var p = Base(ProtocolKind.Http);
        p.Options[Keys.Tls] = "true";
        Assert.DoesNotContain(EngineKind.Xray, Registry().CandidatesFor(p, OsFamily.Linux));
        Assert.Contains(EngineKind.SingBox, Registry().CandidatesFor(p, OsFamily.Linux));
        var ex = Assert.Throws<EngineException>(() =>
            new XrayConfigBuilder().Build(p, ProfileSecrets.Empty, new BuildContext(new AppSettings())));
        Assert.Equal("error.engine.transport.singbox", ex.LocalizationKey);
    }

    [Fact]
    public void Shadowsocks_plugin_forces_singbox()
    {
        var p = Base(ProtocolKind.Shadowsocks);
        p.Options[Keys.Method] = "aes-256-gcm";
        p.Options[Keys.Plugin] = "obfs-local;obfs=http";
        Assert.DoesNotContain(EngineKind.Xray, Registry().CandidatesFor(p, OsFamily.Linux));
        var ex = Assert.Throws<EngineException>(() =>
            new XrayConfigBuilder().Build(p,
                new ProfileSecrets { Values = { [Keys.Password] = "x" } },
                new BuildContext(new AppSettings())));
        Assert.Equal("error.engine.transport.singbox", ex.LocalizationKey);
    }

    [Fact]
    public void Hysteria2_pinSHA256_is_honest_not_silent()
    {
        var p = Base(ProtocolKind.Hysteria2);
        p.Options["pinSHA256"] = "abc";
        Assert.Throws<NotSupportedException>(() =>
            new SingBoxConfigBuilder().Build(p,
                new ProfileSecrets { Values = { [Keys.Password] = "x" } },
                new BuildContext(new AppSettings())));
    }

    [Fact]
    public void Hysteria2_obfs_without_password_omits_empty_secret()
    {
        var p = Base(ProtocolKind.Hysteria2);
        p.Options[Keys.Obfs] = "salamander";
        var root = new SingBoxConfigBuilder().Build(p,
            new ProfileSecrets { Values = { [Keys.Password] = "x" } },
            new BuildContext(new AppSettings()));
        var json = root.ToJsonString();
        Assert.DoesNotContain("\"password\":\"\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Clash_reality_spiderx_matches_uri_parser()
    {
        var yaml = """
            proxies:
              - name: r
                type: vless
                server: example.com
                port: 443
                uuid: 00000000-0000-4000-8000-000000000000
                network: tcp
                tls: true
                reality-opts:
                  public-key: PK
                  short-id: SID
                  spider-x: SPX
            """;
        var result = new ProfileImporter().ImportText(yaml);
        var profile = Assert.Single(result.Profiles).Profile;
        Assert.Equal("SPX", profile.Option(Keys.RealitySpiderX));
    }

    [Fact]
    public void Vless_reality_round_trip_keeps_spx_in_xray()
    {
        var link = "vless://00000000-0000-4000-8000-000000000000@example.com:443?security=reality&pbk=PK&sid=SID&spx=SPX&fp=chrome&sni=example.com#R";
        var result = new ProfileImporter().ImportText(link);
        var imported = Assert.Single(result.Profiles);
        Assert.Equal("SPX", imported.Profile.Option(Keys.RealitySpiderX));
        var root = new XrayConfigBuilder().Build(imported.Profile,
            new ProfileSecrets { Values = { [Keys.Uuid] = "00000000-0000-4000-8000-000000000000" } },
            new BuildContext(new AppSettings()));
        Assert.Contains("SPX", root.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_pings_automatically_and_filters_working()
    {
        var root = RepositoryRoot();
        var wizard = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "ViewModels", "AddServerWizardViewModel.cs"));
        Assert.Contains("CheckPreviewLatency()", wizard, StringComparison.Ordinal);
        Assert.Contains("SortPreviewByLatency = true", wizard, StringComparison.Ordinal);
        // Auto ping stays silent in mock and under the safety lock.
        Assert.Contains("Services.IsMock", wizard, StringComparison.Ordinal);
        Assert.Contains("NetworkSafetyGuard.IsLocked", wizard, StringComparison.Ordinal);
        var connections = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "ViewModels", "ConnectionsViewModel.cs"));
        Assert.Contains("OnlyWorking", connections, StringComparison.Ordinal);
        Assert.Contains("Latencies.TryGetValue", connections, StringComparison.Ordinal);
        Assert.Contains("Reachable", connections, StringComparison.Ordinal);
        var view = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Views", "ConnectionsView.axaml"));
        Assert.Contains("connections.filter.working", view, StringComparison.Ordinal);
        var en = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", "en.json"));
        var ru = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", "ru.json"));
        Assert.Contains("connections.filter.working", en, StringComparison.Ordinal);
        Assert.Contains("connections.filter.working", ru, StringComparison.Ordinal);
    }

    [Fact]
    public void Working_only_filter_keeps_reachable_and_hides_unmeasured()
    {
        var ok = Guid.NewGuid();
        var down = Guid.NewGuid();
        var unmeasured = Guid.NewGuid();
        var latencies = new Dictionary<Guid, Bydlo.Core.Diagnostics.ServerLatencyResult>
        {
            [ok] = new(true, TimeSpan.FromMilliseconds(50), false),
            [down] = new(false, TimeSpan.Zero, false),
        };
        static bool Working(Guid id, IReadOnlyDictionary<Guid, Bydlo.Core.Diagnostics.ServerLatencyResult> map) =>
            map.TryGetValue(id, out var probe) && probe.Reachable;
        Assert.True(Working(ok, latencies));
        Assert.False(Working(down, latencies));
        Assert.False(Working(unmeasured, latencies));
        Assert.False(Working(Guid.NewGuid(), new Dictionary<Guid, Bydlo.Core.Diagnostics.ServerLatencyResult>()));
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BYDLO.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
