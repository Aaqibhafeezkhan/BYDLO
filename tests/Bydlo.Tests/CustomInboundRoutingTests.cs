using System.Text.Json.Nodes;
using Bydlo.Core.Config;
using Bydlo.Core.Engines;
using Bydlo.Core.Import;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

public sealed class CustomInboundRoutingTests
{
    [Theory]
    [InlineData(ConnectionMode.LocalProxy, "mixed-in")]
    [InlineData(ConnectionMode.Tun, "tun-in")]
    public void Sing_box_remaps_entry_and_preserves_unbound_rules(ConnectionMode mode, string tag)
    {
        var root = Source(true);
        root["inbounds"] = JsonNode.Parse("""[{"type":"mixed","tag":"entry"}]""");
        root["route"]!["rules"] = JsonNode.Parse("""
            [{"inbound":["entry"],"domain":["blocked.invalid"],"action":"reject"},
             {"domain_suffix":["direct.invalid"],"outbound":"direct"}]
            """);
        var original = root.ToJsonString();
        var config = Build(root, true, mode);
        var rules = config["route"]!["rules"]!.AsArray();
        var bound = Assert.Single(rules.OfType<JsonObject>(), rule => rule.ContainsKey("inbound"));
        Assert.Equal(new[] { tag }, bound["inbound"]!.AsArray().Select(node => (string)node!));
        Assert.Equal("reject", (string?)bound["action"]);
        Assert.Equal("blocked.invalid", (string?)bound["domain"]![0]);
        Assert.Contains(rules, rule => JsonNode.DeepEquals(rule, root["route"]!["rules"]![1]));
        Assert.Equal(original, root.ToJsonString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Single_entry_maps_to_all_owned_listeners(bool singBox)
    {
        var root = Source(singBox);
        root["inbounds"] = JsonNode.Parse(singBox
            ? """[{"type":"mixed","tag":"entry"}]"""
            : """[{"protocol":"socks","tag":"entry"}]""");
        SetRules(root, singBox, "[\"entry\"]");
        var config = Build(root, singBox);
        var rule = Assert.Single(config[singBox ? "route" : "routing"]!["rules"]!.AsArray()
            .OfType<JsonObject>(), rule => rule.ContainsKey(singBox ? "inbound" : "inboundTag"));
        Assert.Equal(singBox ? new[] { "mixed-in" } : ["socks-in", "http-in"],
            rule[singBox ? "inbound" : "inboundTag"]!.AsArray().Select(node => (string)node!));
    }

    [Fact]
    public void Xray_preserves_separate_socks_and_http_policies_and_unbound_rule()
    {
        var root = Source(false);
        root["inbounds"] = JsonNode.Parse("""
            [{"protocol":"socks","tag":"old-socks"},{"protocol":"http","tag":"old-http"}]
            """);
        root["routing"]!["rules"] = JsonNode.Parse("""
            [{"type":"field","inboundTag":["old-socks"],"outboundTag":"block"},
             {"type":"field","inboundTag":["old-http"],"outboundTag":"direct"},
             {"type":"field","domain":["unbound.invalid"],"outboundTag":"direct"}]
            """);
        var config = Build(root, false);
        var rules = config["routing"]!["rules"]!.AsArray();
        Assert.Equal("socks-in", (string?)rules[0]!["inboundTag"]![0]);
        Assert.Equal("block", (string?)rules[0]!["outboundTag"]);
        Assert.Equal("http-in", (string?)rules[1]!["inboundTag"]![0]);
        Assert.Equal("direct", (string?)rules[1]!["outboundTag"]);
        Assert.True(JsonNode.DeepEquals(root["routing"]!["rules"]![2], rules[2]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Equivalent_collapsed_inputs_are_accepted_but_different_membership_is_rejected(bool singBox)
    {
        var root = Source(singBox);
        root["inbounds"] = JsonNode.Parse(singBox
            ? """[{"type":"mixed","tag":"one"},{"type":"mixed","tag":"two"}]"""
            : """[{"protocol":"socks","tag":"one"},{"protocol":"socks","tag":"two"}]""");
        SetRules(root, singBox, "[\"one\",\"two\"]");
        Build(root, singBox);
        SetRules(root, singBox, "[\"one\"]");
        RejectAtImportAndBuild(root, singBox);
    }

    [Theory]
    [InlineData(true, "[\"missing\"]")]
    [InlineData(false, "[\"missing\"]")]
    [InlineData(true, "[]")]
    [InlineData(false, "[]")]
    [InlineData(true, "null")]
    [InlineData(false, "null")]
    [InlineData(true, "[5]")]
    [InlineData(false, "[5]")]
    [InlineData(false, "\"entry\"")]
    public void Rejects_invalid_or_unresolved_inbound_predicates(bool singBox, string references)
    {
        var root = Source(singBox);
        root["inbounds"] = JsonNode.Parse("""[{"tag":"entry"}]""");
        SetRules(root, singBox, references);
        RejectAtImportAndBuild(root, singBox);
    }

    [Theory]
    [InlineData(true, "[]")]
    [InlineData(false, "[]")]
    [InlineData(true, "[{\"tag\":\"entry\"},{\"tag\":\"entry\"}]")]
    [InlineData(false, "[{\"tag\":\"entry\"},{}]")]
    [InlineData(true, "[null]")]
    [InlineData(false, "[null]")]
    public void Rejects_unresolvable_source_listeners(bool singBox, string inbounds)
    {
        var root = Source(singBox);
        root["inbounds"] = JsonNode.Parse(inbounds);
        SetRules(root, singBox, "[\"entry\"]");
        RejectAtImportAndBuild(root, singBox);
    }

    [Fact]
    public void Xray_rejects_referenced_protocol_without_an_unambiguous_replacement()
    {
        var root = Source(false);
        root["inbounds"] = JsonNode.Parse("""
            [{"protocol":"dokodemo-door","tag":"entry"},{"protocol":"http","tag":"web"}]
            """);
        SetRules(root, false, "[\"entry\"]");
        RejectAtImportAndBuild(root, false);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unbound_rules_do_not_require_source_listeners(bool singBox)
    {
        var root = Source(singBox);
        root[singBox ? "route" : "routing"]!["rules"] = JsonNode.Parse(singBox
            ? """[{"domain":["unbound.invalid"],"outbound":"direct"}]"""
            : """[{"type":"field","domain":["unbound.invalid"],"outboundTag":"direct"}]""");
        var config = Build(root, singBox);
        Assert.True(JsonNode.DeepEquals(root[singBox ? "route" : "routing"]!["rules"],
            config[singBox ? "route" : "routing"]!["rules"]));
    }

    [Fact]
    public void Sing_box_accepts_scalar_inbound_predicate()
    {
        var root = Source(true);
        root["inbounds"] = JsonNode.Parse("""[{"tag":"entry"}]""");
        SetRules(root, true, "\"entry\"");
        Assert.Equal("mixed-in", (string?)Build(root, true)["route"]!["rules"]![0]!["inbound"]![0]);
    }

    private static JsonObject Source(bool singBox) => JsonNode.Parse(singBox
        ? """{"outbounds":[{"type":"direct","tag":"direct"}],"route":{"rules":[]}}"""
        : """{"outbounds":[{"protocol":"freedom","tag":"direct"},{"protocol":"blackhole","tag":"block"}],"routing":{"rules":[]}}""")!.AsObject();

    private static void SetRules(JsonObject root, bool singBox, string references) =>
        root[singBox ? "route" : "routing"]!["rules"] = JsonNode.Parse(singBox
            ? "[{\"inbound\":" + references + ",\"action\":\"reject\"}]"
            : "[{\"type\":\"field\",\"inboundTag\":" + references + ",\"outboundTag\":\"block\"}]");

    private static JsonObject Build(JsonObject root, bool singBox, ConnectionMode mode = ConnectionMode.LocalProxy)
    {
        var imported = Assert.Single(new ProfileImporter().ImportText(root.ToJsonString()).Profiles);
        var context = Context(mode);
        return singBox ? new SingBoxConfigBuilder().Build(imported.Profile, imported.Secrets, context)
            : new XrayConfigBuilder().Build(imported.Profile, imported.Secrets, context);
    }

    private static BuildContext Context(ConnectionMode mode) => new(new AppSettings
    {
        Mode = mode,
        Routing = new RoutingSettings { BypassPrivateNetworks = false, PreserveMeshVpnRanges = false },
    });

    private static void RejectAtImportAndBuild(JsonObject root, bool singBox)
    {
        var result = new ProfileImporter().ImportText(root.ToJsonString());
        Assert.Empty(result.Profiles);
        Assert.Equal("import.json.unsafe", Assert.Single(result.Errors));
        var profile = new Profile { Protocol = singBox ? ProtocolKind.SingBoxConfig : ProtocolKind.XrayConfig };
        var secrets = new ProfileSecrets { Values = { [Keys.RawConfig] = root.ToJsonString() } };
        var exception = Assert.Throws<EngineException>(() => singBox
            ? new SingBoxConfigBuilder().Build(profile, secrets, Context(ConnectionMode.LocalProxy))
            : new XrayConfigBuilder().Build(profile, secrets, Context(ConnectionMode.LocalProxy)));
        Assert.Equal("import.json.unsafe", exception.LocalizationKey);
    }
}
