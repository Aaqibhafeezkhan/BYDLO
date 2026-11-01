using System.Text.Json.Nodes;
using Bydlo.Core.Config;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Import;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

public class ConfigBuilderTests
{
    private static ImportedProfile Import(string text) => Assert.Single(new ProfileImporter().ImportText(text).Profiles);

    private static JsonObject SingBox(ImportedProfile p, AppSettings? s = null) =>
        new SingBoxConfigBuilder().Build(p.Profile, p.Secrets, new BuildContext(s ?? new AppSettings(), 9090, "sec"));

    [Fact]
    public void SingBox_vless_reality_outbound()
    {
        var root = SingBox(Import("vless://11111111-2222-3333-4444-555555555555@example.com:443?security=reality&sni=www.microsoft.com&fp=chrome&pbk=PBK&sid=ab&flow=xtls-rprx-vision"));
        var o = root["outbounds"]![0]!.AsObject();
        Assert.Equal("vless", (string?)o["type"]);
        Assert.Equal("proxy", (string?)o["tag"]);
        Assert.Equal("xtls-rprx-vision", (string?)o["flow"]);
        Assert.True((bool)o["tls"]!["reality"]!["enabled"]!);
        Assert.Equal("PBK", (string?)o["tls"]!["reality"]!["public_key"]);
        Assert.Equal("chrome", (string?)o["tls"]!["utls"]!["fingerprint"]);
    }

    [Fact]
    public void SingBox_uses_current_schema_without_legacy_fields()
    {
        var json = new SingBoxConfigBuilder().BuildJson(
            Import("trojan://pw@t.example.com:443").Profile,
            Import("trojan://pw@t.example.com:443").Secrets,
            new BuildContext(new AppSettings { Routing = { BlockDomains = ["ads.example"] } }));
        var root = JsonNode.Parse(json)!.AsObject();

        // DNS servers use the 1.12+ "type" format.
        Assert.All(root["dns"]!["servers"]!.AsArray(), s => Assert.NotNull(s!["type"]));
        // No legacy special outbounds (block/dns were removed in 1.13).
        Assert.DoesNotContain(root["outbounds"]!.AsArray(), o => (string?)o!["type"] is "block" or "dns");
        // Blocking is a rule action.
        Assert.Contains(root["route"]!["rules"]!.AsArray(), r => (string?)r!["action"] == "reject");
        Assert.Equal("127.0.0.1", (string?)root["inbounds"]![0]!["listen"]);
    }

    [Fact]
    public void SingBox_tun_mode_keeps_mesh_ranges_out_of_the_tunnel()
    {
        var settings = new AppSettings { Mode = ConnectionMode.Tun };
        var root = SingBox(Import("trojan://pw@t.example.com:443"), settings);
        var tun = root["inbounds"]![0]!.AsObject();
        Assert.Equal("tun", (string?)tun["type"]);
        Assert.Null(tun["stack"]); // deprecated in sing-box 1.15
        var excluded = tun["route_exclude_address"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Contains("100.64.0.0/10", excluded);
    }

    [Fact]
    public void SingBox_hysteria2_with_obfs_and_port_hopping()
    {
        var o = SingBox(Import("hy2://auth@hy.example.com:443,20000-30000/?obfs=salamander&obfs-password=op&sni=hy.example.com"))["outbounds"]![0]!;
        Assert.Equal("hysteria2", (string?)o["type"]);
        Assert.Equal("salamander", (string?)o["obfs"]!["type"]);
        Assert.Equal("20000:30000", (string?)o["server_ports"]![0]);
        Assert.Equal("h3", (string?)o["tls"]!["alpn"]![0]);
    }

    [Fact]
    public void SingBox_wireguard_is_an_endpoint()
    {
        var conf = "[Interface]\nPrivateKey = yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=\nAddress = 10.8.0.2/32\n\n[Peer]\nPublicKey = xTIBA5rboUvnH4htodjb6e697QjLERt1NAB4mZqp8Dg=\nAllowedIPs = 0.0.0.0/0\nEndpoint = 203.0.113.5:51820\n";
        var root = SingBox(Import(conf));
        var ep = root["endpoints"]![0]!;
        Assert.Equal("wireguard", (string?)ep["type"]);
        Assert.Equal("203.0.113.5", (string?)ep["peers"]![0]!["address"]);
        Assert.DoesNotContain(root["outbounds"]!.AsArray(), o => (string?)o!["type"] == "wireguard");
    }

    [Fact]
    public void Xray_handles_xhttp_and_registry_routes_it_to_xray()
    {
        var p = Import("vless://id@x.example.com:443?security=tls&type=xhttp&path=%2Fx&mode=auto&sni=x.example.com");
        var root = new XrayConfigBuilder().Build(p.Profile, p.Secrets, new BuildContext(new AppSettings()));
        var stream = root["outbounds"]![0]!["streamSettings"]!;
        Assert.Equal("xhttp", (string?)stream["network"]);
        Assert.Equal("/x", (string?)stream["xhttpSettings"]!["path"]);

        var registry = new EngineRegistry(() => new MockEngine())
            .Register(new EngineDescriptor { Kind = EngineKind.SingBox, DisplayName = "sb", Upstream = "", License = "", Protocols = SingBoxConfigBuilder.Supported, Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented } }, () => new MockEngine(), 10)
            .Register(new EngineDescriptor { Kind = EngineKind.Xray, DisplayName = "x", Upstream = "", License = "", Protocols = XrayConfigBuilder.Supported, Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented } }, () => new MockEngine(), 20);
        Assert.Equal([EngineKind.Xray], registry.CandidatesFor(p.Profile, OsFamily.Windows));
        Assert.Equal(EngineKind.SingBox, registry.CandidatesFor(Import("trojan://a@b.example.com:443").Profile, OsFamily.Windows)[0]);
    }

    [Fact]
    public void SingBox_rejects_xhttp()
    {
        var p = Import("vless://id@x.example.com:443?security=tls&type=xhttp");
        Assert.Throws<NotSupportedException>(() => SingBox(p));
    }

    [Fact]
    public void SingBox_forces_ip_check_domains_through_proxy()
    {
        var settings = new AppSettings
        {
            Routing =
            {
                DirectDomains = ["ipwho.is"],
                ProxyOnlyListed = true,
                ProxyDomains = ["example.com"],
            },
        };
        var rules = SingBox(Import("trojan://pw@t.example.com:443"), settings)["route"]!["rules"]!.AsArray();

        var forced = rules
            .Select((rule, index) => (rule, index))
            .First(x => x.rule!["domain_suffix"] is JsonArray d && d.Any(n => (string?)n == "ipwho.is") && (string?)x.rule["outbound"] == "proxy");
        var direct = rules
            .Select((rule, index) => (rule, index))
            .First(x => x.rule!["domain_suffix"] is JsonArray d && d.Any(n => (string?)n == "ipwho.is") && (string?)x.rule["outbound"] == "direct");

        Assert.True(forced.index < direct.index);
    }

    [Fact]
    public void Xray_forces_ip_check_domains_through_proxy()
    {
        var settings = new AppSettings { Routing = { DirectDomains = ["ipwho.is"] } };
        var p = Import("vless://id@x.example.com:443?security=tls&sni=x.example.com");
        var rules = new XrayConfigBuilder()
            .Build(p.Profile, p.Secrets, new BuildContext(settings))["routing"]!["rules"]!.AsArray();

        var forced = rules
            .Select((rule, index) => (rule, index))
            .First(x => x.rule!["domain"] is JsonArray d && d.Any(n => (string?)n == "domain:ipwho.is") && (string?)x.rule["outboundTag"] == "proxy");
        var direct = rules
            .Select((rule, index) => (rule, index))
            .First(x => x.rule!["domain"] is JsonArray d && d.Any(n => (string?)n == "domain:ipwho.is") && (string?)x.rule["outboundTag"] == "direct");

        Assert.True(forced.index < direct.index);
    }
}
