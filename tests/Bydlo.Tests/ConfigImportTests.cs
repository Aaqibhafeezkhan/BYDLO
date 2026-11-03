using System.Text.Json;
using Bydlo.Core.Config;
using Bydlo.Core.Import;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

public sealed class ConfigImportTests
{
    [Fact]
    public void Parses_mixed_clash_proxies_and_reports_skipped_rows_without_credentials()
    {
        var result = new ProfileImporter().ImportText("""
            proxies:
              - { name: One, type: vless, server: one.invalid, port: 443, uuid: id, tls: true }
              - { name: Two, type: vmess, server: two.invalid, port: 443, uuid: id }
              - { name: Three, type: trojan, server: three.invalid, port: 443, password: pass }
              - { name: Four, type: ss, server: four.invalid, port: 443, cipher: aes-128-gcm, password: pass }
              - { name: Five, type: hysteria2, server: five.invalid, port: 443, password: pass }
              - { name: Six, type: tuic, server: six.invalid, port: 443, uuid: id, password: pass }
              - { name: Secret, type: invalid, password: skipped-secret }
            """);

        Assert.Equal(6, result.Profiles.Count);
        Assert.Equal("import.clash.skipped|7", Assert.Single(result.Errors));
        Assert.All(result.Profiles, item => Assert.DoesNotContain("pass", JsonSerializer.Serialize(item.Profile)));
    }

    [Theory]
    [InlineData("type", "sing-box", ProtocolKind.SingBoxConfig)]
    [InlineData("protocol", "xray", ProtocolKind.XrayConfig)]
    public void Imports_custom_outbounds_but_substitutes_the_local_listeners(string key, string engine, ProtocolKind protocol)
    {
        var raw = engine == "sing-box"
            ? """{"inbounds":[{"type":"mixed","listen":"0.0.0.0","listen_port":2222}],"outbounds":[{"type":"trojan","tag":"remote","server":"proxy.invalid","server_port":443,"password":"hidden"}]}"""
            : """{"inbounds":[{"protocol":"http","listen":"0.0.0.0","port":2222}],"outbounds":[{"protocol":"trojan","tag":"remote","settings":{"servers":[{"address":"proxy.invalid","port":443,"password":"hidden"}]}}]}""";
        var imported = Assert.Single(new ProfileImporter().ImportText(raw).Profiles);
        Assert.Equal(protocol, imported.Profile.Protocol);
        Assert.DoesNotContain("hidden", JsonSerializer.Serialize(imported.Profile));
        Assert.Contains("hidden", imported.Secrets[Keys.RawConfig]);
        var settings = new AppSettings { LocalProxyPort = 3131 };
        var context = new BuildContext(settings, 3132, "loopback-token");
        var config = protocol == ProtocolKind.SingBoxConfig
            ? new SingBoxConfigBuilder().Build(imported.Profile, imported.Secrets, context)
            : new XrayConfigBuilder().Build(imported.Profile, imported.Secrets, context);
        Assert.Equal("127.0.0.1", (string?)config["inbounds"]![0]!["listen"]);
        Assert.Equal(3131, (int?)config["inbounds"]![0]![key == "type" ? "listen_port" : "port"]);
        Assert.Equal("remote", (string?)config["outbounds"]![0]!["tag"]);
    }

    [Theory]
    [InlineData("""{"outbounds":[{"type":"direct","tag":"direct"}],"experimental":{"clash_api":{"external_controller":"0.0.0.0:9090"}}}""")]
    [InlineData("""{"outbounds":[{"type":"trojan","tag":"remote","tls":{"certificate_path":"/private/cert"}}]}""")]
    [InlineData("""{"outbounds":[{"protocol":"freedom","tag":"direct"}],"routing":{"rules":[{"domain":["ext:/private/geosite.dat:tag"]}]}}""")]
    public void Rejects_external_files_and_supplied_control_apis(string raw)
    {
        var result = new ProfileImporter().ImportText(raw);
        Assert.Empty(result.Profiles);
        Assert.Equal("import.json.unsafe", Assert.Single(result.Errors));
    }
}
