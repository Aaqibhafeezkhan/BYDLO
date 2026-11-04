using System.Globalization;
using System.Net.Http.Headers;

namespace Bydlo.Core.Import;

public sealed record SubscriptionPreview(ImportResult Import, SubscriptionMetadata Metadata);

/// <summary>Optional non-secret counters supplied by the subscription server.</summary>
public sealed record SubscriptionMetadata(long? Upload = null, long? Download = null, long? Total = null, DateTimeOffset? Expires = null,
    string? Title = null, long? UpdateIntervalHours = null, string? SupportUrl = null, string? Announce = null)
{
    public const int MaxTitleLength = 100;
    public const int MaxTextLength = 500;
    public const long MaxUpdateIntervalHours = 168;

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

    /// <summary>
    /// Reads Marzban/Remnawave/3x-ui panel headers. Everything is untrusted display text:
    /// trimmed, length-capped, and (for links and intervals) shape-checked.
    /// </summary>
    public static SubscriptionMetadata Parse(string? userinfo, HttpResponseHeaders? headers)
    {
        var metadata = Parse(userinfo);
        if (headers is null)
        {
            return metadata;
        }
        return metadata with
        {
            Title = Text(First(headers, "profile-title"), MaxTitleLength),
            UpdateIntervalHours = Interval(First(headers, "profile-update-interval")),
            SupportUrl = Link(First(headers, "support-url")),
            Announce = Text(First(headers, "announce"), MaxTextLength),
        };
    }

    private static string? First(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) : null;

    private static string? Text(string? value, int max)
    {
        value = value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value.Length > max ? value[..max] : value;
    }

    private static long? Interval(string? value) =>
        long.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var hours) && hours is >= 1 and <= MaxUpdateIntervalHours
            ? hours : null;

    private static string? Link(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrEmpty(value) || value.Length > MaxTextLength ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            ? null : value;
    }
}
