using Bydlo.Core.Config;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

public sealed class DpiConfigTests
{
    private static readonly ProfileSecrets Secrets = new() { Values = { [Keys.Uuid] = "test" } };

    [Fact]
    public void SingBox_applies_defaults_and_profile_can_override_them()
    {
        var profile = MakeProfile();
        var settings = new AppSettings { DpiDefaults = new DpiSettings { Fragment = "record", Mux = "smux", Padding = true } };
        var builder = new SingBoxConfigBuilder();
        var outbound = builder.Build(profile, Secrets, new BuildContext(settings))["outbounds"]![0]!;
        Assert.True((bool?)outbound["tls"]!["record_fragment"]);
        Assert.Equal("smux", (string?)outbound["multiplex"]!["protocol"]);
        Assert.True((bool?)outbound["multiplex"]!["padding"]);

        profile.Options[DpiSettings.FragmentKey] = "off";
        profile.Options[DpiSettings.MuxKey] = "off";
        profile.Options[DpiSettings.PaddingKey] = "false";
        outbound = builder.Build(profile, Secrets, new BuildContext(settings))["outbounds"]![0]!;
        Assert.Null(outbound["tls"]!["record_fragment"]);
        Assert.Null(outbound["multiplex"]);
    }

    [Fact]
    public void Xray_applies_fragmentation_xhttp_padding_and_xudp()
    {
        var profile = MakeProfile();
        profile.Options[Keys.Transport] = "xhttp";
        var settings = new AppSettings
        {
            DpiDefaults = new DpiSettings { Fragment = "tls", Mux = "xudp", Padding = true, Size = "120-150", Interval = "1-4" },
        };
        var outbounds = new XrayConfigBuilder().Build(profile, Secrets, new BuildContext(settings))["outbounds"]!;
        Assert.Equal("proxy-fragment", (string?)outbounds[0]!["streamSettings"]!["sockopt"]!["dialerProxy"]);
        Assert.Equal("120-150", (string?)outbounds[0]!["streamSettings"]!["xhttpSettings"]!["xPaddingBytes"]);
        Assert.Equal(16, (int?)outbounds[0]!["mux"]!["xudpConcurrency"]);
        Assert.Equal("1-4", (string?)outbounds[1]!["settings"]!["fragment"]!["interval"]);
    }

    [Theory]
    [InlineData(EngineKind.SingBox, "smux")]
    [InlineData(EngineKind.Xray, "xray")]
    public void Mux_rejects_vision(EngineKind engine, string mux)
    {
        var profile = MakeProfile();
        profile.Options[Keys.Flow] = "xtls-rprx-vision";
        var settings = new DpiSettings { Mux = mux };
        Assert.Equal("profile.dpi.vision", Assert.Throws<EngineException>(() => settings.Validate(profile, engine)).LocalizationKey);
    }

    private static Profile MakeProfile() => new()
    {
        Protocol = ProtocolKind.Vless,
        Host = "server.invalid",
        Port = 443,
        Options = { [Keys.Security] = "tls" },
    };
}
