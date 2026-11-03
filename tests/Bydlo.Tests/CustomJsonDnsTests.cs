using System.Text.Json.Nodes;
using Bydlo.Core.Config;
using Bydlo.Core.Engines;
using Bydlo.Core.Import;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

public sealed class CustomJsonDnsTests
{
    [Theory]
    [InlineData(ConnectionMode.LocalProxy, false, false)]
    [InlineData(ConnectionMode.LocalProxy, true, true)]
    [InlineData(ConnectionMode.Tun, false, true)]
    [InlineData(ConnectionMode.Tun, true, false)]
    public void Generated_dns_declares_remote_and_direct_sets_once_and_preserves_custom_routes(
        ConnectionMode mode, bool directDns, bool customRoute)
    {
        var routing = new RoutingSettings();
        if (directDns) { routing.DirectDnsRuleSets = ["audit", "audit"]; }
        else { routing.RemoteDnsRuleSets = ["audit", "audit"]; }
        var settings = new AppSettings { Mode = mode, Routing = routing };
        var sets = new Dictionary<string, string> { ["audit"] = "cache/audit.srs", ["unused"] = "cache/unused.srs" };
        var config = Build(settings, sets, customRoute: customRoute);

        var declaration = Assert.Single(config["route"]!["rule_set"]!.AsArray())!;
        Assert.Equal("audit", (string?)declaration["tag"]);
        Assert.Equal("local", (string?)declaration["type"]);
        Assert.Equal("binary", (string?)declaration["format"]);
        Assert.Equal(sets["audit"], (string?)declaration["path"]);
        var dnsRule = Assert.Single(config["dns"]!["rules"]!.AsArray())!;
        Assert.Equal(directDns ? "local" : "remote", (string?)dnsRule["server"]);
        Assert.All(dnsRule["rule_set"]!.AsArray(), reference =>
            Assert.Equal((string?)declaration["tag"], (string?)reference));
        Assert.Equal("remote", (string?)config["route"]!["final"]);
        if (customRoute)
        {
            Assert.Contains(config["route"]!["rules"]!.AsArray(), rule =>
                (string?)rule?["domain_suffix"]?[0] == "example.invalid"
                && (string?)rule?["outbound"] == "remote");
        }
    }

    [Fact]
    public void Shared_remote_and_direct_dns_set_has_one_declaration()
    {
        var settings = new AppSettings { Routing = { RemoteDnsRuleSets = ["audit"], DirectDnsRuleSets = ["audit"] } };
        var config = Build(settings, new Dictionary<string, string> { ["audit"] = "cache/audit.srs" });
        Assert.Single(config["route"]!["rule_set"]!.AsArray());
        Assert.Equal(2, config["dns"]!["rules"]!.AsArray().Count);
        Assert.All(config["dns"]!["rules"]!.AsArray(), rule =>
            Assert.Equal("audit", (string?)rule!["rule_set"]![0]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Generated_dns_rejects_missing_cached_sets(bool directDns, bool nullCache)
    {
        var routing = new RoutingSettings();
        if (directDns) { routing.DirectDnsRuleSets = ["missing"]; }
        else { routing.RemoteDnsRuleSets = ["missing"]; }
        var error = Assert.Throws<EngineException>(() => Build(new AppSettings { Routing = routing },
            nullCache ? null : new Dictionary<string, string>()));
        Assert.Equal("routing.preset.missing", error.LocalizationKey);
    }

    [Fact]
    public void Supplied_dns_is_preserved_without_adding_or_requiring_bydlo_sets()
    {
        var settings = new AppSettings { Routing = { RemoteDnsRuleSets = ["missing"], DirectDnsRuleSets = ["missing"] } };
        var config = Build(settings, null, customDns: true);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(CustomDns), config["dns"]));
        Assert.Null(config["route"]!["rule_set"]);
    }

    [Fact]
    public void Generated_dns_without_set_rules_does_not_declare_unrelated_cache_entries()
    {
        var config = Build(new AppSettings(), new Dictionary<string, string> { ["unused"] = "cache/unused.srs" });
        Assert.Null(config["route"]!["rule_set"]);
    }

    private const string CustomDns = """{"servers":[{"type":"local","tag":"custom"}],"final":"custom"}""";

    private static JsonObject Build(AppSettings settings, IReadOnlyDictionary<string, string>? sets,
        bool customDns = false, bool customRoute = false)
    {
        var root = JsonNode.Parse("""
            {"outbounds":[{"type":"trojan","tag":"remote","server":"proxy.invalid","server_port":443,"password":"fake"}]}
            """)!.AsObject();
        if (customDns) { root["dns"] = JsonNode.Parse(CustomDns); }
        if (customRoute)
        {
            root["route"] = JsonNode.Parse("""
                {"final":"remote","rules":[{"domain_suffix":["example.invalid"],"outbound":"remote"}]}
                """);
        }
        var imported = Assert.Single(new ProfileImporter().ImportText(root.ToJsonString()).Profiles);
        return new SingBoxConfigBuilder().Build(imported.Profile, imported.Secrets, new BuildContext(settings, RuleSets: sets));
    }
}
