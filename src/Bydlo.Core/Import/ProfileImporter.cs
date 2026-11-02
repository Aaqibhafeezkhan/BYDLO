using Bydlo.Core.Import.Parsers;
using Bydlo.Core.Models;

namespace Bydlo.Core.Import;

public sealed record ImportResult(IReadOnlyList<ImportedProfile> Profiles, IReadOnlyList<string> Errors)
{
    public bool IsEmpty => Profiles.Count == 0;
}

/// <summary>
/// Turns anything the user pastes, drops or scans (share links, multi-line lists, base64 subscriptions,
/// WireGuard / AmneziaWG / OpenVPN files, QR payloads) into profiles.
/// </summary>
public sealed class ProfileImporter
{
    private readonly IReadOnlyList<IProfileParser> _linkParsers =
    [
        new VlessParser(),
        new VmessParser(),
        new TrojanParser(),
        new ShadowsocksParser(),
        new Hysteria2Parser(),
        new TuicParser(),
        new WireGuardUriParser(),
        new ProxyParser(),
    ];

    private readonly WireGuardConfParser _wireGuard = new();
    private readonly OpenVpnParser _openVpn = new();

    /// <summary>True when the text is a single http(s) URL that looks like a subscription rather than a proxy.</summary>
    public static bool LooksLikeSubscriptionUrl(string text, bool allowRoot = false)
    {
        var t = text.Trim();
        if (t.Contains('\n') || !Uri.TryCreate(t, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        return uri.UserInfo.Length == 0 && (uri.AbsolutePath.Length > 1 || uri.Query.Length > 1 || allowRoot && uri.IsDefaultPort);
    }

    public ImportResult ImportText(string text, string? sourceName = null)
    {
        var profiles = new List<ImportedProfile>();
        var errors = new List<string>();
        var trimmed = (text ?? "").Trim().TrimStart((char)0xFEFF);
        if (trimmed.Length == 0)
        {
            return new ImportResult(profiles, ["Nothing to import."]);
        }

        if (_wireGuard.CanParse(trimmed))
        {
            return Single(_wireGuard, trimmed, sourceName);
        }

        if (_openVpn.CanParse(trimmed))
        {
            return Single(_openVpn, trimmed, sourceName);
        }

        if (trimmed.StartsWith('{'))
        {
            return new ImportResult(profiles, ["Importing full sing-box / Xray JSON configs is not supported yet. Use share links or subscription URLs."]);
        }

        // Base64 subscription body.
        if (!trimmed.Contains("://", StringComparison.Ordinal) && ImportHelpers.TryDecodeBase64(trimmed, out var decoded) && decoded.Contains("://", StringComparison.Ordinal))
        {
            trimmed = decoded;
        }

        foreach (var line in trimmed.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var parser = _linkParsers.FirstOrDefault(p => p.CanParse(line));
            if (parser is null)
            {
                errors.Add($"Unsupported line: {Shorten(line)}");
                continue;
            }

            try
            {
                profiles.Add(parser.Parse(line));
            }
            catch (FormatException ex)
            {
                errors.Add($"{ex.Message} ({Shorten(line)})");
            }
            catch (ArgumentException ex)
            {
                errors.Add($"{ex.Message} ({Shorten(line)})");
            }
        }

        return new ImportResult(profiles, errors);
    }

    public async Task<ImportResult> ImportFileAsync(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return new ImportResult([], [$"File not found: {info.Name}"]);
        }

        if (info.Length > 2 * 1024 * 1024)
        {
            return new ImportResult([], ["File is too large to be a configuration (max 2 MB)."]);
        }

        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return ImportText(text, Path.GetFileNameWithoutExtension(path));
    }

    private static ImportResult Single(IProfileParser parser, string text, string? sourceName)
    {
        try
        {
            var imported = parser.Parse(text);
            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                imported.Profile.Name = sourceName;
            }

            return new ImportResult([imported], []);
        }
        catch (FormatException ex)
        {
            return new ImportResult([], [ex.Message]);
        }
    }

    // Never echo full links back: they contain credentials.
    private static string Shorten(string line)
    {
        var scheme = line.IndexOf("://", StringComparison.Ordinal);
        return scheme > 0 ? line[..scheme] + "://…" : line.Length > 16 ? line[..16] + "…" : line;
    }
}

/// <summary>Downloads subscription content. Network access happens only when the user asks for it.</summary>
public sealed class SubscriptionClient(HttpClient http, ProfileImporter importer)
{
    private const int MaxBytes = 5 * 1024 * 1024;

    public async Task<ImportResult> FetchAsync(string url, CancellationToken ct = default) =>
        (await FetchPreviewAsync(url, ct).ConfigureAwait(false)).Import;

    public async Task<SubscriptionPreview> FetchPreviewAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return new(new ImportResult([], ["Subscription URL must start with https://"]), new());
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("BYDLO/0.1");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new(new ImportResult([], [$"Subscription server returned HTTP {(int)response.StatusCode}."]), new());
        }

        if (response.Content.Headers.ContentLength > MaxBytes)
        {
            return new(new ImportResult([], ["Subscription is too large."]), new());
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaxBytes)
            { return new(new ImportResult([], ["Subscription is too large."]), new()); }
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        using var reader = new StreamReader(buffer);
        var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        var metadata = SubscriptionMetadata.Parse(response.Headers.TryGetValues("subscription-userinfo", out var info) ? info.FirstOrDefault() : null);
        return new(importer.ImportText(body), metadata);
    }
}
