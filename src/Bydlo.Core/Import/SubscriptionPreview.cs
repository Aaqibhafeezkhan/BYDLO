using System.Globalization;

namespace Bydlo.Core.Import;

public sealed record SubscriptionPreview(ImportResult Import, SubscriptionMetadata Metadata);

/// <summary>Optional non-secret counters supplied by the subscription server.</summary>
public sealed record SubscriptionMetadata(long? Upload = null, long? Download = null, long? Total = null, DateTimeOffset? Expires = null)
{
    public static SubscriptionMetadata Parse(string? header)
    {
        var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in (header ?? "").Split(';'))
        {
            var pair = field.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            { values[pair[0]] = value; }
        }
        long? Get(string key) => values.TryGetValue(key, out var value) ? value : null;
        DateTimeOffset? expires = null;
        if (Get("expire") is > 0 and <= 253402300799)
        { expires = DateTimeOffset.FromUnixTimeSeconds(values["expire"]); }
        return new(Get("upload"), Get("download"), Get("total"), expires);
    }
}
