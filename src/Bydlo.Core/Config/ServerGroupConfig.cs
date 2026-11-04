using System.Text.Json.Nodes;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;

namespace Bydlo.Core.Config;

public static class ServerGroupSupport
{
    public static bool Supports(EngineKind engine, Profile profile) =>
        profile.Protocol is not (ProtocolKind.SingBoxConfig or ProtocolKind.XrayConfig or ProtocolKind.ShadowTls)
        && (engine == EngineKind.SingBox ? SingBoxConfigBuilder.Supported.Contains(profile.Protocol)
            && profile.Option(Keys.Transport) is not ("xhttp" or "splithttp")
            : engine == EngineKind.Xray && XrayConfigBuilder.Supported.Contains(profile.Protocol)
            && profile.Option(Keys.Transport) is not ("h2" or "http"));

    public static void Validate(ServerGroup group, IReadOnlyList<ChainHop> members, EngineKind engine)
    {
        group.Validate(members.Select(m => m.Profile).ToList());
        if (group.Engine != engine || members.Any(m => !Supports(engine, m.Profile))) { throw ServerGroup.Invalid(); }
    }
}

public sealed partial class SingBoxConfigBuilder
{
    public JsonObject BuildGroup(ServerGroup group, IReadOnlyList<ChainHop> members, BuildContext ctx)
    {
        ServerGroupSupport.Validate(group, members, EngineKind.SingBox);
        var root = Build(members[0].Profile, members[0].Secrets, ctx);
        root.Remove("endpoints");
        var outbounds = new JsonArray();
        var tags = members.Select(m => ServerGroup.Tag(m.Profile.Id)).ToArray();
        var selection = new JsonObject
        {
            ["type"] = group.Mode == ServerGroupMode.UrlTest ? "urltest" : "selector",
            ["tag"] = ProxyTag, ["outbounds"] = ToArray(tags),
        };
        if (group.Mode == ServerGroupMode.UrlTest)
        {
            selection["url"] = group.ProbeUrl;
            selection["interval"] = $"{group.IntervalSeconds}s";
            selection["tolerance"] = group.ToleranceMs;
        }
        else { selection["default"] = ServerGroup.Tag(group.InitialProfileId(members)); }
        outbounds.Add(selection);
        var endpoints = new JsonArray();
        foreach (var member in members)
        {
            var tag = ServerGroup.Tag(member.Profile.Id);
            if (member.Profile.Protocol == ProtocolKind.WireGuard)
            {
                endpoints.Add(BuildWireGuardEndpoint(member.Profile, member.Secrets, tag));
            }
            else { outbounds.Add(BuildOutbound(member.Profile, member.Secrets, ctx.Settings.DpiDefaults, tag)); }
        }
        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
        root["outbounds"] = outbounds;
        if (endpoints.Count > 0) { root["endpoints"] = endpoints; }
        return root;
    }
}

public sealed partial class XrayConfigBuilder
{
    public JsonObject BuildGroup(ServerGroup group, IReadOnlyList<ChainHop> members, BuildContext ctx)
    {
        ServerGroupSupport.Validate(group, members, EngineKind.Xray);
        var auxiliary = new List<JsonObject>();
        var proxies = members.Select(m => BuildOutbound(m.Profile, m.Secrets, ctx.Settings.DpiDefaults,
            auxiliary, ServerGroup.Tag(m.Profile.Id))).ToList();
        // Xray selectors match prefixes. A fragment dialer must never enter the member pool.
        foreach (var dialer in auxiliary)
        {
            var oldTag = (string)dialer["tag"]!;
            var newTag = "aux-" + oldTag;
            dialer["tag"] = newTag;
            foreach (var proxy in proxies)
            {
                if ((string?)proxy["streamSettings"]?["sockopt"]?["dialerProxy"] == oldTag)
                { proxy["streamSettings"]!["sockopt"]!["dialerProxy"] = newTag; }
            }
        }
        var selected = ServerGroup.Tag(group.InitialProfileId(members));
        // Xray defaults to the first outbound when no rule matches: fail closed, never bypass a dead group.
        proxies.Insert(0, new JsonObject { ["tag"] = "group-unavailable", ["protocol"] = "blackhole" });
        proxies.AddRange(auxiliary);
        var root = BuildCore(ctx, proxies, "proxy");
        var routing = root["routing"]!.AsObject();
        var rules = routing["rules"]!.AsArray();
        foreach (var rule in rules.OfType<JsonObject>().Where(r => (string?)r["outboundTag"] == "proxy"))
        {
            rule.Remove("outboundTag");
            rule["balancerTag"] = "proxy";
        }
        rules.Add(new JsonObject { ["type"] = "field", ["network"] = "tcp,udp", ["balancerTag"] = "proxy" });
        routing["balancers"] = new JsonArray(new JsonObject
        {
            ["tag"] = "proxy", ["selector"] = Arr(members.Select(m => ServerGroup.Tag(m.Profile.Id))),
            ["fallbackTag"] = group.Mode == ServerGroupMode.Manual ? selected : "group-unavailable",
            ["strategy"] = new JsonObject { ["type"] = "leastPing" },
        });
        root["observatory"] = new JsonObject
        {
            ["subjectSelector"] = Arr(members.Select(m => ServerGroup.Tag(m.Profile.Id))),
            ["probeUrl"] = group.ProbeUrl, ["probeInterval"] = $"{group.IntervalSeconds}s", ["enableConcurrency"] = true,
        };
        // Manual mode observes nothing: the explicit fallback is the only eligible target.
        if (group.Mode == ServerGroupMode.Manual) { root["observatory"]!["subjectSelector"] = new JsonArray("no-group-members"); }
        if (root["api"] is JsonObject api) { api["services"] = new JsonArray("RoutingService", "ObservatoryService", "StatsService"); }
        return root;
    }
}
