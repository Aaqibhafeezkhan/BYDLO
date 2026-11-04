using Bydlo.Core.Config;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

/// <summary>Issue #74: DPI tricks reach both engines correctly and incompatible mixes are rejected.</summary>
public sealed class DpiMatrixTests
{
    private static readonly ProfileSecrets Secrets = new()
    {
        Values =
        {
            [Keys.Uuid] = "test",
            [Keys.Password] = "test-password",
            [Keys.Username] = "test-user",
        },
    };

    private static Profile TlsProfile(ProtocolKind protocol = ProtocolKind.Vless, string security = "tls") => new()
    {
        Protocol = protocol, Host = "server.invalid", Port = 443,
        Options = { [Keys.Security] = security },
    };

    private static DpiSettings Dpi(string fragment = "off", string mux = "off", bool padding = false) =>
        new() { Fragment = fragment, Mux = mux, Padding = padding };

    [Theory]
    [InlineData("tls")]
    [InlineData("record")]
    public void SingBox_applies_fragment_modes_over_tls(string fragment)
    {
        var settings = new AppSettings { DpiDefaults = Dpi(fragment: fragment) };
        var outbound = new SingBoxConfigBuilder().Build(TlsProfile(), Secrets, new BuildContext(settings))["outbounds"]![0]!;
        Assert.True((bool?)outbound["tls"]![fragment == "record" ? "record_fragment" : "fragment"]);
    }

    [Fact]
    public void SingBox_fragment_needs_tls()
    {
        var settings = new AppSettings { DpiDefaults = Dpi(fragment: "tls") };
        var error = Assert.Throws<EngineException>(() =>
            new SingBoxConfigBuilder().Build(TlsProfile(security: "none"), Secrets, new BuildContext(settings)));
        Assert.Equal("profile.dpi.unsupported", error.LocalizationKey);
    }

    [Theory]
    [InlineData(ProtocolKind.Hysteria2)]
    [InlineData(ProtocolKind.Tuic)]
    [InlineData(ProtocolKind.Naive)]
    [InlineData(ProtocolKind.Socks5)]
    public void SingBox_rejects_fragment_without_tls_outbound(ProtocolKind protocol)
    {
        var settings = new AppSettings { DpiDefaults = Dpi(fragment: "tls") };
        var error = Assert.Throws<EngineException>(() =>
            new SingBoxConfigBuilder().Build(TlsProfile(protocol), Secrets, new BuildContext(settings)));
        Assert.Equal("profile.dpi.unsupported", error.LocalizationKey);
    }

    [Theory]
    [InlineData(ProtocolKind.Hysteria2)]
    [InlineData(ProtocolKind.Tuic)]
    [InlineData(ProtocolKind.Naive)]
    [InlineData(ProtocolKind.Socks5)]
    public void SingBox_rejects_mux_for_non_mux_protocols(ProtocolKind protocol)
    {
        var settings = new AppSettings { DpiDefaults = Dpi(mux: "smux") };
        var error = Assert.Throws<EngineException>(() =>
            new SingBoxConfigBuilder().Build(TlsProfile(protocol), Secrets, new BuildContext(settings)));
        Assert.Equal("profile.dpi.unsupported", error.LocalizationKey);
    }

    [Theory]
    [InlineData("smux")]
    [InlineData("yamux")]
    [InlineData("h2mux")]
    public void Xray_rejects_singbox_mux(string mux)
    {
        var settings = new AppSettings { DpiDefaults = Dpi(mux: mux) };
        var error = Assert.Throws<EngineException>(() =>
            new XrayConfigBuilder().Build(TlsProfile(), Secrets, new BuildContext(settings)));
        Assert.Equal("profile.dpi.unsupported", error.LocalizationKey);
    }

    [Theory]
    [InlineData("xray")]
    [InlineData("xudp")]
    public void SingBox_rejects_xray_mux(string mux)
    {
        var settings = new AppSettings { DpiDefaults = Dpi(mux: mux) };
        var error = Assert.Throws<EngineException>(() =>
            new SingBoxConfigBuilder().Build(TlsProfile(), Secrets, new BuildContext(settings)));
        Assert.Equal("profile.dpi.unsupported", error.LocalizationKey);
    }

    [Fact]
    public void SingBox_padding_requires_mux()
    {
        var settings = new AppSettings { DpiDefaults = Dpi(mux: "off", padding: true) };
        var error = Assert.Throws<EngineException>(() =>
            new SingBoxConfigBuilder().Build(TlsProfile(), Secrets, new BuildContext(settings)));
        Assert.Equal("profile.dpi.unsupported", error.LocalizationKey);
    }

    [Fact]
    public void Xray_plain_mux_emits_concurrency_without_xudp()
    {
        var settings = new AppSettings { DpiDefaults = Dpi(mux: "xray") };
        var outbound = new XrayConfigBuilder().Build(TlsProfile(), Secrets, new BuildContext(settings))["outbounds"]![0]!;
        Assert.True((bool?)outbound["mux"]!["enabled"]);
        Assert.Equal(8, (int?)outbound["mux"]!["concurrency"]);
    }

    [Fact]
    public void Xray_rejects_mux_for_non_mux_protocols()
    {
        var settings = new DpiSettings { Mux = "xray" };
        var profile = TlsProfile(ProtocolKind.Tuic);
        var error = Assert.Throws<EngineException>(() => settings.Validate(profile, EngineKind.Xray));
        Assert.Equal("profile.dpi.unsupported", error.LocalizationKey);
    }

    [Fact]
    public void Xray_padding_requires_xhttp()
    {
        var settings = new AppSettings { DpiDefaults = Dpi(padding: true) };
        var error = Assert.Throws<EngineException>(() =>
            new XrayConfigBuilder().Build(TlsProfile(), Secrets, new BuildContext(settings)));
        Assert.Equal("profile.dpi.unsupported", error.LocalizationKey);
    }

    [Fact]
    public void Xray_record_fragment_is_singbox_only()
    {
        var settings = new AppSettings { DpiDefaults = Dpi(fragment: "record") };
        var error = Assert.Throws<EngineException>(() =>
            new XrayConfigBuilder().Build(TlsProfile(), Secrets, new BuildContext(settings)));
        Assert.Equal("profile.dpi.unsupported", error.LocalizationKey);
    }

    [Fact]
    public void Xray_fragment_over_reality_without_via()
    {
        var settings = new AppSettings { DpiDefaults = Dpi(fragment: "tls") };
        var outbounds = new XrayConfigBuilder().Build(TlsProfile(security: "reality"), Secrets, new BuildContext(settings))["outbounds"]!;
        Assert.Equal("proxy-fragment", (string?)outbounds[0]!["streamSettings"]!["sockopt"]!["dialerProxy"]);
    }

    [Fact]
    public void Profile_override_beats_defaults()
    {
        var settings = new AppSettings { DpiDefaults = Dpi(fragment: "tls", mux: "smux") };
        var profile = TlsProfile();
        profile.Options[DpiSettings.FragmentKey] = "off";
        profile.Options[DpiSettings.MuxKey] = "off";
        var outbound = new SingBoxConfigBuilder().Build(profile, Secrets, new BuildContext(settings))["outbounds"]![0]!;
        Assert.Null(outbound["tls"]!["fragment"]);
        Assert.Null(outbound["multiplex"]);
    }

    [Theory]
    [InlineData("1-16384", "0-5000", false)]
    [InlineData("0-100", "10-20", true)]
    [InlineData("100-200", "10-99999", true)]
    [InlineData("abc", "10-20", true)]
    [InlineData("100-200", "xyz", true)]
    public void Bad_ranges_are_rejected_with_explanation(string size, string interval, bool throws)
    {
        var settings = new DpiSettings { Size = size, Interval = interval };
        if (throws)
        {
            var error = Assert.Throws<EngineException>(() => settings.ValidateFormat());
            Assert.Equal("profile.dpi.invalid", error.LocalizationKey);
        }
        else
        {
            settings.ValidateFormat();
        }
    }
}
