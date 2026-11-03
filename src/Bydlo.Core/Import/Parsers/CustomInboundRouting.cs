using System.Text.Json.Nodes;

namespace Bydlo.Core.Import.Parsers;

/// <summary>Preserves inbound predicates when BYDLO replaces custom listeners.</summary>
internal static class CustomInboundRouting
{
    // A null target validates the mapping at import time without changing the stored config.
    public static void Map(JsonObject root, bool singBox, string[]? targets = null)
    {
        var key = singBox ? "inbound" : "inboundTag";
        var rules = (root[singBox ? "route" : "routing"]?["rules"] as JsonArray)?
            .OfType<JsonObject>().Where(rule => rule.ContainsKey(key)).ToArray() ?? [];
        if (rules.Length == 0) { return; }

        var inbounds = root["inbounds"] as JsonArray;
        if (inbounds is not { Count: > 0 }) { throw new FormatException(); }
        var groups = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var node in inbounds)
        {
            if (node is not JsonObject inbound ||
                inbound["tag"] is not JsonValue tagValue || !tagValue.TryGetValue<string>(out var tag) ||
                string.IsNullOrWhiteSpace(tag) || groups.ContainsKey(tag))
            {
                throw new FormatException();
            }

            // A single source listener supplies the entire replacement. With several Xray
            // listeners, SOCKS and HTTP remain separate; other protocols need explicit selection.
            groups[tag] = singBox ? [0] : inbounds.Count == 1 ? [0, 1] :
                (string?)inbound["protocol"] switch
                {
                    "socks" => [0],
                    "http" => [1],
                    _ => [],
                };
        }

        foreach (var rule in rules)
        {
            var references = ReadReferences(rule[key], singBox);
            if (references.Any(tag => !groups.TryGetValue(tag, out var group) || group.Length == 0))
            {
                throw new FormatException();
            }
            var mapped = references.SelectMany(tag => groups[tag]).Distinct().Order().ToArray();
            // Every source feeding the same replacement must agree on membership in each
            // predicate. Otherwise collapsing listeners would silently broaden its policy.
            if (groups.Any(pair => pair.Value.Intersect(mapped).Any() && !references.Contains(pair.Key)))
            {
                throw new FormatException();
            }
            if (targets is not null)
            {
                rule[key] = new JsonArray(mapped.Select(index => (JsonNode)JsonValue.Create(targets[index])!).ToArray());
            }
        }
    }

    private static HashSet<string> ReadReferences(JsonNode? node, bool allowScalar)
    {
        if (allowScalar && node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
        {
            return new HashSet<string>([text], StringComparer.Ordinal);
        }
        if (node is not JsonArray { Count: > 0 } array) { throw new FormatException(); }
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is not JsonValue scalar || !scalar.TryGetValue<string>(out var tag) || string.IsNullOrWhiteSpace(tag))
            {
                throw new FormatException();
            }
            references.Add(tag);
        }
        return references;
    }
}
