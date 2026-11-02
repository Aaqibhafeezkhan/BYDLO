using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Bydlo.Engines.TgWsProxy;

/// <summary>
/// Cloudflare-proxied domains maintained by Flowseal for the fallback path (<c>kws{dc}.{domain}</c>).
/// The list is shipped lightly obfuscated upstream and refreshed hourly from the upstream repository.
/// </summary>
internal sealed partial class CfProxyDomains
{
    public static readonly Uri ListUri = new("https://raw.githubusercontent.com/Flowseal/tg-ws-proxy/main/.github/cfproxy-domains.txt");

    /// <summary>GitHub addresses used when DNS for raw.githubusercontent.com does not resolve correctly.</summary>
    public static readonly IReadOnlyDictionary<string, string> PinnedHosts = new Dictionary<string, string>
    {
        ["raw.githubusercontent.com"] = "185.199.109.133",
    };

    public const int MinValidDomains = 3;

    private static readonly int[] KnownDcs = [1, 2, 3, 4, 5, 203];

    // Same encoded defaults as upstream proxy/config.py (v1.11.4).
    private static readonly string[] EncodedDefaults =
    [
        "virkgj.com", "vmmzovy.com", "mkuosckvso.com", "zaewayzmplad.com", "twdmbzcm.com",
        "awzwsldi.com", "clngqrflngqin.com", "tjacxbqtj.com", "bxaxtxmrw.com", "dmohrsgmohcrwb.com",
        "vwbmtmoi.com", "khgrre.com", "ulihssf.com", "tmhqsdqmfpmk.com", "xwuwoqbm.com",
        "orgcnunpj.com", "zhkuldz.com", "zypoljnslxa.com", "efabnxaowuzs.com", "zaftuzsftqdq.com",
    ];

    private readonly Lock _gate = new();
    private List<string> _domains = [];
    private Dictionary<int, string> _dcToDomain = [];

    public static IReadOnlyList<string> Defaults { get; } = [.. EncodedDefaults.Select(Decode)];

    public IReadOnlyList<string> Domains
    {
        get
        {
            lock (_gate)
            {
                return [.. _domains];
            }
        }
    }

    /// <summary>Upstream's decoding: a Caesar shift by the count of letters, then the real suffix replaces <c>.com</c>.</summary>
    public static string Decode(string encoded)
    {
        if (!encoded.EndsWith(".com", StringComparison.Ordinal))
        {
            return encoded;
        }

        var body = encoded[..^4];
        var shift = body.Count(char.IsLetter);
        var sb = new StringBuilder(body.Length + 6);
        foreach (var c in body)
        {
            if (!char.IsLetter(c))
            {
                sb.Append(c);
                continue;
            }

            var origin = c > '`' ? 'a' : 'A';
            sb.Append((char)((((c - origin - shift) % 26) + 26) % 26 + origin));
        }

        return sb.Append(".co.uk").ToString();
    }

    /// <summary>Lines of the upstream list (comments skipped), decoded and validated.</summary>
    public static List<string> ParseList(string text) =>
        Normalize(text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(Decode));

    public static List<string> Normalize(IEnumerable<string> domains)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in domains)
        {
            var domain = raw.Trim().ToLowerInvariant();
            if (IsValidDomain(domain) && seen.Add(domain))
            {
                result.Add(domain);
            }
        }

        return result;
    }

    public static bool IsValidDomain(string domain)
    {
        if (domain.Length is 0 or > 253 || domain.StartsWith('.') || domain.EndsWith('.'))
        {
            return false;
        }

        var labels = domain.Split('.');
        if (labels.Length < 2 || labels.Any(l => l.Length is 0 or > 63 || l[0] == '-' || l[^1] == '-' || !l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            return false;
        }

        return labels[^1].Length >= 2 && labels[^1].Any(char.IsAsciiLetter);
    }

    public void Update(IReadOnlyList<string> domains)
    {
        lock (_gate)
        {
            if (domains.Count == 0 || (_domains.Count == domains.Count && !_domains.Except(domains).Any()))
            {
                return;
            }

            _domains = [.. domains];
            _dcToDomain = KnownDcs.ToDictionary(dc => dc, _ => _domains[RandomNumberGenerator.GetInt32(_domains.Count)]);
        }
    }

    /// <summary>The domain that last worked for this DC first, then the rest in random order.</summary>
    public IReadOnlyList<string> CandidatesFor(int dc)
    {
        lock (_gate)
        {
            var current = _dcToDomain.GetValueOrDefault(dc);
            var rest = _domains.Where(d => d != current).ToArray();
            RandomNumberGenerator.Shuffle(rest.AsSpan());
            return current is null ? rest : [current, .. rest];
        }
    }

    /// <summary>Remembers a working domain; true when it changed.</summary>
    public bool Prefer(int dc, string domain)
    {
        lock (_gate)
        {
            if (_dcToDomain.GetValueOrDefault(dc) == domain)
            {
                return false;
            }

            _dcToDomain[dc] = domain;
            return true;
        }
    }

    /// <summary>Hides Cloudflare fallback domains in logs (as upstream's DomainCensorFilter); telegram.org stays readable.</summary>
    public static string Censor(string line) => DomainPattern().Replace(line, m =>
    {
        var domain = m.Value;
        var normalized = domain.ToLowerInvariant().TrimEnd('.');
        if (normalized == "telegram.org" || normalized.EndsWith(".telegram.org", StringComparison.Ordinal) || normalized.EndsWith(".log", StringComparison.Ordinal))
        {
            return domain;
        }

        var parts = domain.Split('.');
        return string.Join('.', parts.Select((p, i) => i == parts.Length - 1 ? p : p[..(p.Length / 2)] + new string('*', p.Length - (p.Length / 2))));
    });

    [GeneratedRegex(@"(?<![\w-])(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,}(?![\w-])")]
    private static partial Regex DomainPattern();
}
