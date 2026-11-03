using System.Text.Json.Nodes;
using Bydlo.Core.Config;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;

namespace Bydlo.Tests;

/// <summary>Covers issue #203: macOS TUN must not use the fixed bydlo-tun interface name.</summary>
public sealed class TunInterfaceTests
{
    private static JsonObject BuildTun(AppSettings settings, OsFamily os)
    {
        var profile = new Profile { Name = "t", Protocol = ProtocolKind.Trojan, Host = "t.example.com", Port = 443 };
        var secrets = new ProfileSecrets { Values = { [Keys.Password] = "pw-123456" } };
        return new SingBoxConfigBuilder().Build(profile, secrets,
            new BuildContext(settings, Os: os))["inbounds"]![0]!.AsObject();
    }

    private static AppSettings TunSettings() => new() { Mode = ConnectionMode.Tun };

    [Theory]
    [InlineData(OsFamily.Windows)]
    [InlineData(OsFamily.Linux)]
    public void Tun_keeps_fixed_name_outside_macos(OsFamily os)
    {
        var inbound = BuildTun(TunSettings(), os);
        Assert.Equal("bydlo-tun", (string?)inbound["interface_name"]);
    }

    [Fact]
    public void Tun_omits_interface_name_on_macos()
    {
        var inbound = BuildTun(TunSettings(), OsFamily.MacOS);
        Assert.Null(inbound["interface_name"]);
        Assert.Equal("tun-in", (string?)inbound["tag"]);
    }

    [Fact]
    public void Kill_switch_omits_interface_name_on_macos()
    {
        var settings = new AppSettings
        {
            Mode = ConnectionMode.Tun,
            KillSwitch = true,
            Routing = new RoutingSettings { RemoteDns = "1.1.1.1" },
        };
        var inbound = new SingBoxConfigBuilder().BuildKillSwitch(settings, 2080, OsFamily.MacOS)["inbounds"]![0]!.AsObject();
        Assert.Null(inbound["interface_name"]);
        var windows = new SingBoxConfigBuilder().BuildKillSwitch(settings, 2080, OsFamily.Windows)["inbounds"]![0]!.AsObject();
        Assert.Equal("bydlo-tun", (string?)windows["interface_name"]);
    }
}
