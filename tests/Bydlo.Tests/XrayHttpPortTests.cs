using Bydlo.Core.Config;
using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;

namespace Bydlo.Tests;

/// <summary>Covers issue #207: Xray HTTP port (LocalProxyPort+1) is checked for conflicts.</summary>
public sealed class XrayHttpPortTests
{
    [Fact]
    public void Busy_http_port_blocks_xray_capable_profile()
    {
        var detector = new ConflictDetector(new FakePorts(2081));
        var profile = new Profile { Protocol = ProtocolKind.Trojan, Host = "h.invalid", Port = 443 };
        var issues = detector.CheckPrimary(profile, new AppSettings { LocalProxyPort = 2080 }, false, false);
        Assert.Contains(issues, i => i.Code == "port.local-proxy-http");
    }

    [Fact]
    public void Busy_http_port_does_not_block_singbox_only_protocol()
    {
        var detector = new ConflictDetector(new FakePorts(2081));
        var profile = new Profile { Protocol = ProtocolKind.Hysteria2, Host = "h.invalid", Port = 443 };
        var issues = detector.CheckPrimary(profile, new AppSettings { LocalProxyPort = 2080 }, false, false);
        Assert.DoesNotContain(issues, i => i.Code == "port.local-proxy-http");
        Assert.DoesNotContain(issues, i => i.Severity == ConflictSeverity.Blocking);
    }

    [Fact]
    public void Boundary_port_65535_is_rejected_for_xray()
    {
        var detector = new ConflictDetector(new FakePorts());
        var profile = new Profile { Protocol = ProtocolKind.Trojan, Host = "h.invalid", Port = 443 };
        var issues = detector.CheckPrimary(profile, new AppSettings { LocalProxyPort = 65535 }, false, false);
        Assert.Contains(issues, i => i.Severity == ConflictSeverity.Blocking);
    }

    [Fact]
    public void Tg_clash_covers_http_port()
    {
        var detector = new ConflictDetector(new FakePorts());
        var profile = new Profile { Protocol = ProtocolKind.Trojan, Host = "h.invalid", Port = 443 };
        var settings = new AppSettings { LocalProxyPort = 2080, TgWsProxy = new TgWsProxySettings { Port = 2081 } };
        var issues = detector.CheckPrimary(profile, settings, false, tgRunning: true);
        Assert.Contains(issues, i => i.Code == "port.tg-clash");
    }

    [Fact]
    public void Helper_returns_expected_http_ports()
    {
        Assert.Equal(2080, ProxyPorts.HttpPort(2080, EngineKind.SingBox));
        Assert.Equal(2081, ProxyPorts.HttpPort(2080, EngineKind.Xray));
        Assert.Equal(ProxyPorts.MaxLocalProxyPort, 65534);
    }

    [Fact]
    public void Xray_builder_rejects_boundary_port()
    {
        var builder = new XrayConfigBuilder();
        var profile = new Profile { Name = "t", Protocol = ProtocolKind.Trojan, Host = "t.example.com", Port = 443 };
        var secrets = new ProfileSecrets { Values = { [Keys.Password] = "pw" } };
        Assert.Throws<EngineException>(() => builder.Build(profile, secrets,
            new BuildContext(new AppSettings { LocalProxyPort = 65535 })));
    }
}
