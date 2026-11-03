using Bydlo.Core.Models;
using Bydlo.Core.Security;

namespace Bydlo.Core.Routing;

public static class RoutingPresets
{
    private const string GeoSite = "https://raw.githubusercontent.com/SagerNet/sing-geosite/5cd46a0648352abddcabf7e2d300ae2ed07af09a/";
    private const string GeoIp = "https://raw.githubusercontent.com/SagerNet/sing-geoip/eb32cd6ed4a16fcb3bf9a36ccebf28e45716ae84/";

    public static IReadOnlyList<RuleSetSource> Sources(RoutingPreset preset, string country) => preset switch
    {
        RoutingPreset.BlockedOnly =>
        [
            new("blocked-ru", new Uri("https://raw.githubusercontent.com/runetfreedom/russia-v2ray-rules-dat/c7f602289ad10c0d038af1681c2c2834aa7ead14/sing-box/rule-set-geosite/geosite-ru-blocked.srs"),
                "3f0155971b03e3d9ff416e845e97ca23b838a772752168155af92fbc07d9ea6a", 520171),
        ],
        RoutingPreset.ExceptLocal when country == "cn" =>
        [
            new("geosite-cn", new Uri(GeoSite + "geosite-cn.srs"), "9b0a9209546e8cf35792317d4459bf690ba02b172d74b851488b0bca6fb068c1", 56821),
            new("geoip-cn", new Uri(GeoIp + "geoip-cn.srs"), "81ada5cafd9d831adc48776f29cdfe55c9713d9287caeef9de925ef30f66cce4", 34268),
        ],
        RoutingPreset.ExceptLocal =>
        [
            new("geosite-ru", new Uri(GeoSite + "geosite-category-ru.srs"), "66d16eaba254233527e9a581a54a19f21385cb6bb9fdbda2dc3d3947facde4f0", 9024),
            new("geoip-ru", new Uri(GeoIp + "geoip-ru.srs"), "bdca5cdb44cdcf0940001e5952386822a6f4ffa8b2c8eea00340aa6f585c7f9d", 50096),
        ],
        _ => [],
    };

    public static RuleSetSource? Find(string tag) =>
        Sources(RoutingPreset.BlockedOnly, "ru")
            .Concat(Sources(RoutingPreset.ExceptLocal, "ru"))
            .Concat(Sources(RoutingPreset.ExceptLocal, "cn"))
            .FirstOrDefault(source => source.Tag == tag);

    public static IReadOnlyList<RuleSetSource> BuiltInSources =>
        [.. Sources(RoutingPreset.BlockedOnly, "ru"), .. Sources(RoutingPreset.ExceptLocal, "ru"),
            .. Sources(RoutingPreset.ExceptLocal, "cn"), XraySource(RoutingCondition.GeoSite)!, XraySource(RoutingCondition.GeoIp)!];

    public static string SingBoxTag(RoutingCondition condition, string value) => (condition, value) switch
    {
        (RoutingCondition.GeoSite, "cn") => "geosite-cn",
        (RoutingCondition.GeoSite, "category-ru") => "geosite-ru",
        (RoutingCondition.GeoIp, "cn") => "geoip-cn",
        (RoutingCondition.GeoIp, "ru") => "geoip-ru",
        _ => value,
    };

    public static string XrayCode(RoutingCondition condition, string value) => (condition, value) switch
    {
        (RoutingCondition.GeoSite, "geosite-cn") => "cn",
        (RoutingCondition.GeoSite, "geosite-ru") => "category-ru",
        (RoutingCondition.GeoIp, "geoip-cn") => "cn",
        (RoutingCondition.GeoIp, "geoip-ru") => "ru",
        _ => value,
    };

    public static bool UnsupportedXrayRule(RoutingSettings settings, OrderedRoutingRule rule) =>
        rule.Condition is RoutingCondition.GeoSite or RoutingCondition.GeoIp &&
        (rule.Value.Trim() == "blocked-ru" || settings.RuleSetSources.Any(source =>
            !IsXraySource(source) && source.Tag == rule.Value.Trim()));

    public static RuleSetSource? XraySource(RoutingCondition condition) => condition switch
    {
        RoutingCondition.GeoSite => new("geosite.dat", new Uri("https://github.com/v2fly/domain-list-community/releases/download/20261102203016/dlc.dat"),
            "78188a409b130ea5537a56941cca0d2f88d33f4a5e3a1bc0a7c2db5681ff4c0b", 2405469),
        RoutingCondition.GeoIp => new("geoip.dat", new Uri("https://github.com/v2fly/geoip/releases/download/202611022136/geoip.dat"),
            "eedb24b3b9fcba27e06b92c0428dab90d9496083091ae4cf39e11387fd99d0e2", 23876117),
        _ => null,
    };

    public static string CacheDirectory(string dataDirectory) => Path.Combine(dataDirectory, "routing-rule-sets");

    public static RuleSetSource? Find(RoutingSettings settings, string tag) =>
        settings.RuleSetSources.FirstOrDefault(source => source.Tag == tag) ?? Find(tag);

    public static RuleSetSource? XraySource(RoutingSettings settings, RoutingCondition condition)
    {
        var fallback = XraySource(condition);
        return fallback is null ? null :
            settings.RuleSetSources.FirstOrDefault(source => source.Tag == fallback.Tag) ?? fallback;
    }

    public static bool IsXraySource(RuleSetSource source) => source.Tag is "geosite.dat" or "geoip.dat";

    public static IReadOnlyList<RuleSetSource> XraySources(RoutingSettings settings)
    {
        var sources = settings.OrderedRules.Select(rule => XraySource(settings, rule.Condition))
            .OfType<RuleSetSource>().ToList();
        // XRAY_LOCATION_ASSET replaces the whole asset directory. geoip:private still needs geoip.dat.
        if (sources.Count > 0 && settings.BypassPrivateNetworks)
        {
            sources.Add(XraySource(settings, RoutingCondition.GeoIp)!);
        }
        return sources.DistinctBy(source => source.Tag).ToArray();
    }

    public static IReadOnlyList<RuleSetSource> DownloadSources(RoutingSettings settings) =>
        Sources(settings.Preset, settings.PresetCountry)
            .Concat(settings.RuleSetSources)
            .Concat(settings.OrderedRules.Where(rule => rule.Condition is RoutingCondition.GeoSite or RoutingCondition.GeoIp)
                .Select(rule => Find(settings, SingBoxTag(rule.Condition, rule.Value.Trim()))).OfType<RuleSetSource>())
            .Concat(XraySources(settings))
            .Concat(settings.RemoteDnsRuleSets.Concat(settings.DirectDnsRuleSets)
                .Select(tag => Find(settings, tag)).OfType<RuleSetSource>())
            .DistinctBy(source => source.Tag).ToArray();

    public static string CachedPath(string directory, RuleSetSource source) =>
        Path.Combine(directory, IntegrityVerifier.NormalizeDigest(source.Sha256) + ".rules");
}
