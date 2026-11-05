using Bydlo.Core.Config;
using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

/// <summary>Covers issue #353: adjacent HTTP port (2081) belongs to Xray only.
/// sing-box uses a single mixed listener on <see cref="AppSettings.LocalProxyPort"/>.</summary>
public sealed class ConflictPortsByEngineTests
{
    private static Profile Vless(EngineKind? preferred = null) => new()
    {
        Name = "v",
        Protocol = ProtocolKind.Vless,
        Host = "v.invalid",
        Port = 443,
        PreferredEngine = preferred,
    };

    private static EngineRegistry RegistryWithSingBoxAndXray()
    {
        // Mirror production priorities: sing-box wins when both carry the protocol.
        return new EngineRegistry(() => new MockEngine())
            .Register(new EngineDescriptor
            {
                Kind = EngineKind.SingBox,
                DisplayName = "sing-box",
                Upstream = "",
                License = "",
                Protocols = new HashSet<ProtocolKind>(SingBoxConfigBuilder.Supported),
                Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented },
            }, () => new MockEngine(), priority: 10)
            .Register(new EngineDescriptor
            {
                Kind = EngineKind.Xray,
                DisplayName = "xray",
                Upstream = "",
                License = "",
                Protocols = new HashSet<ProtocolKind>(XrayConfigBuilder.Supported),
                Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented },
            }, () => new MockEngine(), priority: 20);
    }

    private static ConnectionManager Manager(EngineRegistry registry, IPortChecker ports) => new(
        registry,
        new InMemorySecretStore(),
        new RootedAppPaths(TestDirs.New("conflict-353")),
        new ConflictDetector(ports),
        new LogHub(new LogRedactor()),
        os: OsFamily.Windows);

    [Fact]
    public void SingBox_vless_ignores_busy_adjacent_http_port()
    {
        var manager = Manager(RegistryWithSingBoxAndXray(), new FakePorts(2081));
        var settings = new AppSettings { LocalProxyPort = 2080 };
        var issues = manager.PreviewConflicts(Vless(EngineKind.SingBox), settings);
        Assert.DoesNotContain(issues, i => i.Code == "port.local-proxy-http");
        Assert.DoesNotContain(issues, i => i.Severity == ConflictSeverity.Blocking);
    }

    [Fact]
    public void Default_vless_prefers_singbox_and_ignores_busy_adjacent_http_port()
    {
        // No explicit preference: sing-box wins (priority 10) so 2081 must not block.
        var manager = Manager(RegistryWithSingBoxAndXray(), new FakePorts(2081));
        var settings = new AppSettings { LocalProxyPort = 2080 };
        var issues = manager.PreviewConflicts(Vless(null), settings);
        Assert.DoesNotContain(issues, i => i.Code == "port.local-proxy-http");
        Assert.DoesNotContain(issues, i => i.Severity == ConflictSeverity.Blocking);
    }

    [Fact]
    public void Xray_vless_blocks_on_busy_adjacent_http_port()
    {
        var manager = Manager(RegistryWithSingBoxAndXray(), new FakePorts(2081));
        var settings = new AppSettings { LocalProxyPort = 2080 };
        var issues = manager.PreviewConflicts(Vless(EngineKind.Xray), settings);
        Assert.Contains(issues, i => i.Code == "port.local-proxy-http" && i.Severity == ConflictSeverity.Blocking);
    }

    [Fact]
    public void Detector_direct_singbox_does_not_block_http()
    {
        var detector = new ConflictDetector(new FakePorts(2081));
        var issues = detector.CheckPrimary(Vless(), new AppSettings { LocalProxyPort = 2080 }, false, false, EngineKind.SingBox);
        Assert.DoesNotContain(issues, i => i.Code == "port.local-proxy-http");
        Assert.DoesNotContain(issues, i => i.Severity == ConflictSeverity.Blocking);
    }

    [Fact]
    public void Detector_direct_xray_blocks_http()
    {
        var detector = new ConflictDetector(new FakePorts(2081));
        var issues = detector.CheckPrimary(Vless(), new AppSettings { LocalProxyPort = 2080 }, false, false, EngineKind.Xray);
        Assert.Contains(issues, i => i.Code == "port.local-proxy-http");
    }

    [Fact]
    public void Detector_unknown_engine_does_not_block_http()
    {
        var detector = new ConflictDetector(new FakePorts(2081));
        var issues = detector.CheckPrimary(Vless(), new AppSettings { LocalProxyPort = 2080 }, false, false, null);
        Assert.DoesNotContain(issues, i => i.Code == "port.local-proxy-http");
        Assert.DoesNotContain(issues, i => i.Severity == ConflictSeverity.Blocking);
    }

    [Fact]
    public void Tg_check_with_singbox_primary_ignores_adjacent_http_port()
    {
        var detector = new ConflictDetector(new FakePorts());
        var settings = new AppSettings { LocalProxyPort = 2080, TgWsProxy = new TgWsProxySettings { Port = 2081 } };
        var issues = detector.CheckTgWsProxy(settings, primaryConnected: true, EngineKind.SingBox);
        Assert.DoesNotContain(issues, i => i.Code == "port.tg-clash");
    }

    [Fact]
    public void Tg_check_with_xray_primary_reports_adjacent_http_clash()
    {
        var detector = new ConflictDetector(new FakePorts());
        var settings = new AppSettings { LocalProxyPort = 2080, TgWsProxy = new TgWsProxySettings { Port = 2081 } };
        var issues = detector.CheckTgWsProxy(settings, primaryConnected: true, EngineKind.Xray);
        Assert.Contains(issues, i => i.Code == "port.tg-clash");
    }

    [Fact]
    public void Tg_check_still_reports_direct_port_clash_for_singbox()
    {
        var detector = new ConflictDetector(new FakePorts());
        var settings = new AppSettings { LocalProxyPort = 2080, TgWsProxy = new TgWsProxySettings { Port = 2080 } };
        var issues = detector.CheckTgWsProxy(settings, primaryConnected: true, EngineKind.SingBox);
        Assert.Contains(issues, i => i.Code == "port.tg-clash");
    }

    [Fact]
    public void Chain_with_singbox_ignores_busy_adjacent_http_port()
    {
        var manager = Manager(RegistryWithSingBoxAndXray(), new FakePorts(2081));
        var hops = new List<Profile> { Vless(), Vless() };
        var settings = new AppSettings { LocalProxyPort = 2080 };
        var issues = manager.PreviewChainConflicts(hops, settings, EngineKind.SingBox);
        Assert.DoesNotContain(issues, i => i.Code == "port.local-proxy-http");
        Assert.DoesNotContain(issues, i => i.Severity == ConflictSeverity.Blocking);
    }

    [Fact]
    public void Chain_with_xray_blocks_on_busy_adjacent_http_port()
    {
        var manager = Manager(RegistryWithSingBoxAndXray(), new FakePorts(2081));
        var hops = new List<Profile> { Vless(), Vless() };
        var settings = new AppSettings { LocalProxyPort = 2080 };
        var issues = manager.PreviewChainConflicts(hops, settings, EngineKind.Xray);
        Assert.Contains(issues, i => i.Code == "port.local-proxy-http");
    }
}
