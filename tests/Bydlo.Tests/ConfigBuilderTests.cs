using Bydlo.Core.Routing;
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

    [Theory]
    [InlineData("chacha20-poly1305", "chacha20-ietf-poly1305")]
    [InlineData("xchacha20-poly1305", "xchacha20-ietf-poly1305")]
    public void Shadowsocks_cipher_aliases_are_normalized_for_sing_box(string alias, string method)
    {
        var root = SingBox(Import($"ss://{alias}:password@ss.example.com:8388"));
        Assert.Equal(method, (string?)root["outbounds"]![0]!["method"]);
    }

    [Fact]
    public void Clash_controller_is_authenticated_and_loopback_only()
    {
        var root = SingBox(Import("trojan://pw@t.example.com:443"));
        Assert.Equal("127.0.0.1:9090", (string?)root["experimental"]!["clash_api"]!["external_controller"]);
        Assert.Equal("sec", (string?)root["experimental"]!["clash_api"]!["secret"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Clash_controller_requires_a_secret(string? secret)
    {
        var imported = Import("trojan://pw@t.example.com:443");
        Assert.ThrowsAny<ArgumentException>(() => new SingBoxConfigBuilder().Build(imported.Profile, imported.Secrets,
            new BuildContext(new AppSettings(), 9090, secret)));
    }

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
        Assert.Null(o["server_port"]);
        Assert.Equal("443:443", (string?)o["server_ports"]![0]);
        Assert.Equal("20000:30000", (string?)o["server_ports"]![1]);
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
    public void Xray_uses_certificate_pin_and_name_instead_of_allow_insecure()
    {
        var pin = new string('a', 64);
        var p = Import($"vless://id@x.example.com:443?security=tls&type=xhttp&allowInsecure=1&pcs={pin}&vcn=x.example.com");
        var root = new XrayConfigBuilder().Build(p.Profile, p.Secrets, new BuildContext(new AppSettings()));
        var tls = root["outbounds"]![0]!["streamSettings"]!["tlsSettings"]!;
        Assert.Equal(pin, (string?)tls["pinnedPeerCertSha256"]);
        Assert.Equal("x.example.com", (string?)tls["verifyPeerCertByName"]);
        Assert.Null(tls["allowInsecure"]);

        p.Profile.Options.Remove(Keys.PinnedPeerCertSha256);
        var error = Assert.Throws<EngineException>(() =>
            new XrayConfigBuilder().Build(p.Profile, p.Secrets, new BuildContext(new AppSettings())));
        Assert.Equal("error.engine.xray.tls.pin", error.LocalizationKey);
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

    [Fact]
    public void Xray_supports_proxy_only_listed_with_catch_all_direct()
    {
        var settings = new AppSettings
        {
            Routing =
            {
                ProxyOnlyListed = true,
                ProxyDomains = ["example.com"],
            },
        };
        var p = Import("vless://id@x.example.com:443?security=tls&sni=x.example.com");
        var rules = new XrayConfigBuilder()
            .Build(p.Profile, p.Secrets, new BuildContext(settings))["routing"]!["rules"]!.AsArray();

        Assert.Contains(rules, r =>
            r!["domain"] is JsonArray d && d.Any(n => (string?)n == "domain:example.com") &&
            (string?)r["outboundTag"] == "proxy");
        var last = rules[^1]!.AsObject();
        Assert.Equal("direct", (string?)last["outboundTag"]);
        Assert.Equal("tcp,udp", (string?)last["network"]);
    }

    [Fact]
    public void Xray_forces_ip_check_through_proxy_in_include_mode()
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
        var p = Import("vless://id@x.example.com:443?security=tls&sni=x.example.com");
        var rules = new XrayConfigBuilder()
            .Build(p.Profile, p.Secrets, new BuildContext(settings))["routing"]!["rules"]!.AsArray();

        var forced = rules
            .Select((rule, index) => (rule, index))
            .First(x => x.rule!["domain"] is JsonArray d && d.Any(n => (string?)n == "domain:ipwho.is") && (string?)x.rule["outboundTag"] == "proxy");
        var catchAll = rules
            .Select((rule, index) => (rule, index))
            .First(x => (string?)x.rule!["outboundTag"] == "direct" && (string?)x.rule["network"] == "tcp,udp");

        Assert.True(forced.index < catchAll.index);
    }

    [Theory]
    [InlineData(ConnectionMode.Tun, false, "direct", "proxy")]
    [InlineData(ConnectionMode.Tun, true, "proxy", "direct")]
    [InlineData(ConnectionMode.LocalProxy, true, null, "proxy")]
    public void SingBox_routes_listed_processes_only_in_tun(
        ConnectionMode mode, bool onlyListed, string? processOutbound, string finalOutbound)
    {
        var settings = new AppSettings
        {
            Mode = mode,
            Routing = { DirectProcesses = ["game.exe"], ProxyOnlyListedProcesses = onlyListed },
        };
        var route = SingBox(Import("trojan://pw@t.example.com:443"), settings)["route"]!;
        Assert.Equal(finalOutbound, (string?)route["final"]);
        var rule = route["rules"]!.AsArray().SingleOrDefault(r => r?["process_name"] is JsonArray);
        Assert.Equal(processOutbound, (string?)rule?["outbound"]);
    }

    [Fact]
    public void Xray_supports_direct_processes()
    {
        var settings = new AppSettings { Routing = { DirectProcesses = ["game.exe"] } };
        var p = Import("vless://id@x.example.com:443?security=tls&sni=x.example.com");
        var rules = new XrayConfigBuilder()
            .Build(p.Profile, p.Secrets, new BuildContext(settings))["routing"]!["rules"]!.AsArray();

        Assert.Contains(rules, r =>
            r!["process"] is JsonArray d && d.Any(n => (string?)n == "game.exe") &&
            (string?)r["outboundTag"] == "direct");
    }
    [Theory]
    [InlineData(RoutingPreset.All, "ru", "proxy", null, null)]
    [InlineData(RoutingPreset.BlockedOnly, "ru", "direct", "blocked-ru", "proxy")]
    [InlineData(RoutingPreset.ExceptLocal, "cn", "proxy", "geosite-cn", "direct")]
    [InlineData(RoutingPreset.ExceptLocal, "ru", "proxy", "geosite-ru", "direct")]
    public void SingBox_applies_the_selected_routing_preset(
        RoutingPreset preset, string country, string final, string? firstRuleSet, string? ruleOutbound)
    {
        var settings = new AppSettings { Routing = { Preset = preset, PresetCountry = country } };
        var profile = Import("trojan://pw@t.example.com:443");
        var sources = Bydlo.Core.Routing.RoutingPresets.Sources(preset, country);
        var paths = sources.ToDictionary(s => s.Tag, s => s.Tag + ".srs");
        var route = new SingBoxConfigBuilder().Build(profile.Profile, profile.Secrets, new BuildContext(settings, RuleSets: paths))["route"]!;
        Assert.Equal(final, (string?)route["final"]);
        Assert.Equal(firstRuleSet, (string?)route["rule_set"]?[0]?["tag"]);
        var rule = route["rules"]!.AsArray().SingleOrDefault(r => r?["rule_set"] is JsonArray);
        Assert.Equal(ruleOutbound, (string?)rule?["outbound"]);
    }

    [Fact]
    public void SingBox_rejects_regional_preset_when_verified_sets_are_missing()
    {
        var settings = new AppSettings { Routing = { Preset = RoutingPreset.BlockedOnly } };
        var profile = Import("trojan://pw@t.example.com:443");
        Assert.Equal("routing.preset.missing",
            Assert.Throws<EngineException>(() => SingBox(profile, settings)).LocalizationKey);
    }
    [Fact]
    public void Ordered_rules_keep_precedence_for_both_engines()
    {
        var settings = new AppSettings
        {
            Routing =
            {
                OrderedRules =
                [
                    new(RoutingCondition.Domain, "example.test", RoutingAction.Direct),
                    new(RoutingCondition.Suffix, "example.test", RoutingAction.Block),
                    new(RoutingCondition.Port, "443", RoutingAction.Proxy),
                ],
            },
        };
        var profile = Import("trojan://pw@t.example.com:443");
        var singBox = SingBox(profile, settings)["route"]!["rules"]!.AsArray();
        var xray = new XrayConfigBuilder().Build(profile.Profile, profile.Secrets, new BuildContext(settings))["routing"]!["rules"]!.AsArray();
        var singBoxStart = singBox.IndexOf(singBox.Single(rule => rule?["domain"] is JsonArray));
        var xrayStart = xray.IndexOf(xray.Single(rule => (string?)rule?["domain"]?[0] == "full:example.test"));
        Assert.Equal("direct", (string?)singBox[singBoxStart]?["outbound"]);
        Assert.Equal("reject", (string?)singBox[singBoxStart + 1]?["action"]);
        Assert.Equal(443, (int?)singBox[singBoxStart + 2]?["port"]?[0]);
        Assert.Equal("direct", (string?)xray[xrayStart]?["outboundTag"]);
        Assert.Equal("block", (string?)xray[xrayStart + 1]?["outboundTag"]);
        Assert.Equal("443", (string?)xray[xrayStart + 2]?["port"]);
    }

    [Theory]
    [InlineData(RoutingCondition.Domain, "example.test", "domain", "full:example.test")]
    [InlineData(RoutingCondition.Suffix, "example.test", "domain_suffix", "domain:example.test")]
    [InlineData(RoutingCondition.Keyword, "example", "domain_keyword", "plain:example")]
    [InlineData(RoutingCondition.Cidr, "192.0.2.0/24", "ip_cidr", "192.0.2.0/24")]
    [InlineData(RoutingCondition.Process, "browser", "process_name", "browser")]
    [InlineData(RoutingCondition.GeoSite, "cn", "rule_set", "geosite:cn")]
    [InlineData(RoutingCondition.GeoIp, "geoip-ru", "rule_set", "geoip:ru")]
    public void Conditions_translate_for_both_engines_and_every_action(RoutingCondition condition, string value, string singKey, string xrayValue)
    {
        var profile = Import("trojan://pw@t.example.com:443");
        foreach (var action in Enum.GetValues<RoutingAction>())
        {
            var settings = new AppSettings { Mode = ConnectionMode.Tun, Routing =
            {
                OrderedRules = [new(condition, value, action)],
            } };
            var paths = new Dictionary<string, string> { ["geosite-cn"] = "fake.srs", ["geoip-ru"] = "fake-ip.srs" };
            var sing = new SingBoxConfigBuilder().Build(profile.Profile, profile.Secrets, new BuildContext(settings, RuleSets: paths))["route"]!["rules"]!
                .AsArray().Single(rule => rule?[singKey] is not null &&
                    (string?)rule[singKey]?[0] == (condition is RoutingCondition.GeoSite or RoutingCondition.GeoIp ? RoutingPresets.SingBoxTag(condition, value) : value));
            Assert.Equal(action == RoutingAction.Block ? "reject" : null, (string?)sing?["action"]);
            Assert.Equal(action == RoutingAction.Block ? null : action == RoutingAction.Direct ? "direct" : "proxy", (string?)sing?["outbound"]);
            if (condition == RoutingCondition.GeoSite) { Assert.Equal("geosite-cn", (string?)sing?[singKey]?[0]); }
            var xrayKey = condition is RoutingCondition.Cidr or RoutingCondition.GeoIp ? "ip" : condition == RoutingCondition.Process ? "process" : "domain";
            var xray = new XrayConfigBuilder().Build(profile.Profile, profile.Secrets, new BuildContext(settings))["routing"]!["rules"]!
                .AsArray().Single(rule => (string?)rule?[xrayKey]?[0] == xrayValue);
            Assert.Equal(action == RoutingAction.Block ? "block" : action == RoutingAction.Direct ? "direct" : "proxy", (string?)xray?["outboundTag"]);
        }
    }
}
