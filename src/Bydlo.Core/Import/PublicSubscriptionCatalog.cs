namespace Bydlo.Core.Import;

/// <summary>
/// One public subscription list. Only link-list formats are listed: Clash YAML
/// and other formats BYDLO cannot parse are excluded on purpose.
/// </summary>
public sealed record PublicSubscriptionSource(string Id, string NameKey, string DescriptionKey, IReadOnlyList<string> Urls);

/// <summary>
/// Built-in catalog of free community subscription lists (issue #39).
/// Nothing is copied into the project: only source links are stored, files are
/// downloaded by the app from the author's mirrors on explicit user action.
/// </summary>
public static class PublicSubscriptionCatalog
{
    private static IReadOnlyList<string> Mirrors(string file) =>
    [
        $"https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/main/{file}",
        $"https://gitlab.com/igareck/vpn-configs-for-russia/-/raw/main/{file}",
        $"https://codeberg.org/igareck/vpn-configs-for-russia/raw/branch/main/{file}",
        $"https://bitbucket.org/igareck/vpn-configs-for-russia/raw/main/{file}",
        $"https://raw.githack.com/igareck/vpn-configs-for-russia/main/{file}",
    ];

    /// <summary>Primary GitHub raw file plus CDN mirrors that work for any public repository.</summary>
    private static IReadOnlyList<string> CdnMirrors(string owner, string repo, string file) =>
    [
        $"https://raw.githubusercontent.com/{owner}/{repo}/main/{file}",
        $"https://cdn.jsdelivr.net/gh/{owner}/{repo}@main/{file}",
        $"https://raw.githack.com/{owner}/{repo}/main/{file}",
        $"https://github.com/{owner}/{repo}/raw/main/{file}",
    ];

    public static IReadOnlyList<PublicSubscriptionSource> Sources { get; } =
    [
        new("igareck-top150", "catalog.igareck.top150", "catalog.igareck.top150.desc",
            Mirrors("BLACK_VLESS_RUS_mobile.txt")),
        new("igareck-vless", "catalog.igareck.vless", "catalog.igareck.vless.desc",
            Mirrors("BLACK_VLESS_RUS.txt")),
        new("igareck-ss", "catalog.igareck.ss", "catalog.igareck.ss.desc",
            Mirrors("BLACK_SS%2BAll_RUS.txt")),
        new("igareck-white-mobile", "catalog.igareck.white.mobile", "catalog.igareck.white.mobile.desc",
            Mirrors("Vless-Reality-White-Lists-Rus-Mobile.txt")),
        new("igareck-white-cidr", "catalog.igareck.white.cidr", "catalog.igareck.white.cidr.desc",
            Mirrors("WHITE-CIDR-RU-checked.txt")),
        // Independent upstreams (issue #136): verified by their maintainers, downloaded on demand.
        new("radikal-verified", "catalog.radikal.verified", "catalog.radikal.verified.desc",
            CdnMirrors("0xRadikal", "Free-v2ray-Configs", "verified/configs_base64.txt")),
        new("matin-vless", "catalog.matin.vless", "catalog.matin.vless.desc",
            CdnMirrors("MatinGhanbari", "v2ray-configs", "subscriptions/filtered/subs/vless.txt")),
    ];
}
