using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;

namespace Bydlo.Core.Routing;

public enum RoutingCondition { Domain, Suffix, Keyword, GeoSite, GeoIp, Cidr, Process, Port }
public enum RoutingAction { Proxy, Direct, Block }

public sealed record OrderedRoutingRule(RoutingCondition Condition, string Value, RoutingAction Action);

internal static class OrderedRoutingRules
{
    public static JsonObject Compile(OrderedRoutingRule rule, bool singBox, ConnectionMode mode, string proxyTag,
        IReadOnlyDictionary<string, string>? ruleSets)
    {
        var value = rule.Value?.Trim();
        if (string.IsNullOrEmpty(value) || value.Any(char.IsControl) ||
            !Enum.IsDefined(rule.Condition) || !Enum.IsDefined(rule.Action) ||
            (rule.Condition == RoutingCondition.Cidr && !IPNetwork.TryParse(value, out _)) ||
            (rule.Condition == RoutingCondition.Port && (!ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port == 0)))
        {
            throw new EngineException("routing.rules.invalid", localizationKey: "routing.rules.invalid");
        }

        var tag = rule.Action switch
        {
            RoutingAction.Proxy => proxyTag,
            RoutingAction.Direct => "direct",
            _ => "block",
        };
        if (singBox)
        {
            var match = rule.Condition switch
            {
                RoutingCondition.Domain => new JsonObject { ["domain"] = new JsonArray(value) },
                RoutingCondition.Suffix => new JsonObject { ["domain_suffix"] = new JsonArray(value) },
                RoutingCondition.Keyword => new JsonObject { ["domain_keyword"] = new JsonArray(value) },
                RoutingCondition.Cidr => new JsonObject { ["ip_cidr"] = new JsonArray(value) },
                RoutingCondition.Process when mode == ConnectionMode.Tun => new JsonObject { ["process_name"] = new JsonArray(value) },
                RoutingCondition.Port => new JsonObject { ["port"] = new JsonArray(int.Parse(value, CultureInfo.InvariantCulture)) },
                RoutingCondition.GeoSite or RoutingCondition.GeoIp when ruleSets?.ContainsKey(value) == true =>
                    new JsonObject { ["rule_set"] = new JsonArray(value) },
                _ => throw new EngineException("routing.rules.unsupported", localizationKey: "routing.rules.unsupported"),
            };
            if (rule.Action == RoutingAction.Block)
            {
                match["action"] = "reject";
            }
            else
            {
                match["outbound"] = tag;
            }
            return match;
        }

        var xray = rule.Condition switch
        {
            RoutingCondition.Domain => new JsonObject { ["domain"] = new JsonArray("full:" + value) },
            RoutingCondition.Suffix => new JsonObject { ["domain"] = new JsonArray("domain:" + value) },
            RoutingCondition.Keyword => new JsonObject { ["domain"] = new JsonArray("plain:" + value) },
            RoutingCondition.Cidr => new JsonObject { ["ip"] = new JsonArray(value) },
            RoutingCondition.GeoSite => new JsonObject { ["domain"] = new JsonArray("geosite:" + GeoCode(value)) },
            RoutingCondition.GeoIp => new JsonObject { ["ip"] = new JsonArray("geoip:" + GeoCode(value)) },
            RoutingCondition.Process => new JsonObject { ["process"] = new JsonArray(value) },
            RoutingCondition.Port => new JsonObject { ["port"] = value },
            _ => throw new EngineException("routing.rules.unsupported", localizationKey: "routing.rules.unsupported"),
        };
        xray["type"] = "field";
        xray["outboundTag"] = tag;
        return xray;
    }

    private static string GeoCode(string value) =>
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '@' or '!')
            ? value
            : throw new EngineException("routing.rules.invalid", localizationKey: "routing.rules.invalid");
}
