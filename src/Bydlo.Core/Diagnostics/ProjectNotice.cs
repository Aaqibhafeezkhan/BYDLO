using System.Text.Json;
using System.Text.RegularExpressions;
using Bydlo.Core.Models;
using Bydlo.Core.Security;

namespace Bydlo.Core.Diagnostics;

public enum ProjectNoticeLevel
{
    Info,
    Critical,
}

/// <summary>
/// Announcement published by the maintainers in <c>notice.json</c> at the repository root (issue #193),
/// e.g. "a fixed release is coming soon". Texts are keyed by language; English is the fallback.
/// </summary>
public sealed record ProjectNotice(
    string Id,
    ProjectNoticeLevel Level,
    IReadOnlyDictionary<string, string> Title,
    IReadOnlyDictionary<string, string> Text,
    string? Url,
    string? HideFromVersion,
    DateTimeOffset? Expires)
{
    /// <summary>False once expired or when the running version is at least <see cref="HideFromVersion"/>.</summary>
    public bool IsActive(string currentVersion, DateTimeOffset now) =>
        (Expires is not { } expires || now < expires) &&
        (HideFromVersion is not { } hideFrom || GitHubUpdateClient.IsNewer(currentVersion, hideFrom));

    public string LocalizedTitle(string language) => Pick(Title, language);

    public string LocalizedText(string language) => Pick(Text, language);

    private static string Pick(IReadOnlyDictionary<string, string> values, string language) =>
        values.TryGetValue(language, out var value) ? value :
        values.TryGetValue("en", out var english) ? english :
        values.Values.FirstOrDefault() ?? "";
}

/// <summary>
/// Reads <c>notice.json</c> from the default branch. Network access happens only from the startup check
/// (with automatic update checks on); tests inject a fake <see cref="HttpMessageHandler"/>.
/// </summary>
public sealed partial class ProjectNoticeClient(Func<HttpMessageHandler>? handlerFactory = null)
{
    public const string NoticeUrl = "https://raw.githubusercontent.com/endercodezz/BYDLO/main/notice.json";

    private const int MaxBytes = 64 * 1024;
    private const int MaxTitleLength = 120;
    private const int MaxTextLength = 600;

    /// <summary>
    /// Parses <c>notice.json</c>. Returns null for an empty notice (no <c>id</c> or no text) and for anything
    /// malformed. Links are kept only when they point into the BYDLO repository on GitHub.
    /// </summary>
    public static ProjectNotice? ParseNotice(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var id = String(root, "id");
            if (id is null || !IdPattern().IsMatch(id))
            {
                return null;
            }
            var text = Localized(root, "text", MaxTextLength);
            if (text.Count == 0)
            {
                return null;
            }
            var level = String(root, "level") is { } value && value.Equals("critical", StringComparison.OrdinalIgnoreCase)
                ? ProjectNoticeLevel.Critical
                : ProjectNoticeLevel.Info;
            var hideFrom = String(root, "hideFromVersion");
            if (hideFrom is not null && !VersionPattern().IsMatch(hideFrom))
            {
                // A typo must not show a notice to every future version.
                return null;
            }
            DateTimeOffset? expires = null;
            if (String(root, "expires") is { } expiresText)
            {
                if (!DateTimeOffset.TryParse(expiresText, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    return null;
                }
                expires = parsed;
            }
            return new ProjectNotice(id, level, Localized(root, "title", MaxTitleLength), text,
                SafeUrl(String(root, "url")), hideFrom, expires);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Only https links to the BYDLO repository; anything else is dropped.</summary>
    public static string? SafeUrl(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var path = uri.AbsolutePath;
        return path.Equals("/endercodezz/BYDLO", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/endercodezz/BYDLO/", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
    }

    public async Task<ProjectNotice?> FetchAsync(CancellationToken ct = default)
    {
        if (handlerFactory is null)
        {
            NetworkSafetyGuard.EnsureAllowed("load the project notice");
        }
        using var http = new HttpClient(handlerFactory?.Invoke() ?? new SocketsHttpHandler())
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = MaxBytes,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(AppVersion.UserAgent);
        using var response = await http.GetAsync(NoticeUrl, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseNotice(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString()?.Trim() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>Accepts a plain string (any language) or an object of language → text.</summary>
    private static Dictionary<string, string> Localized(JsonElement root, string name, int maxLength)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty(name, out var value))
        {
            return result;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            Add("en", value.GetString());
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in value.EnumerateObject())
            {
                if (item.Value.ValueKind == JsonValueKind.String && LanguagePattern().IsMatch(item.Name))
                {
                    Add(item.Name.ToLowerInvariant(), item.Value.GetString());
                }
            }
        }
        return result;

        void Add(string language, string? text)
        {
            var clean = new string((text ?? "").Where(c => !char.IsControl(c) || c == '\n').ToArray()).Trim();
            if (clean.Length > 0)
            {
                result[language] = clean.Length > maxLength ? clean[..maxLength].TrimEnd() + "…" : clean;
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguagePattern();

    [GeneratedRegex(@"^[vV]?\d+(\.\d+){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

/// <summary>
/// Startup policy: notification content needs its own consent (issue #438), never runs while the
/// network is blocked (Mock Mode, development lock). A notice the user dismissed stays hidden until
/// its id changes. Failures show nothing; startup continues.
/// </summary>
public sealed class ProjectNoticeChecker(ProjectNoticeClient client, TimeProvider? timeProvider = null)
{
    public async Task<ProjectNotice?> CheckAsync(AppSettings settings, string currentVersion,
        bool networkBlocked, CancellationToken ct = default)
    {
        if (!FeatureConsent.From(settings).Allows(OptionalFeature.Notifications) || networkBlocked)
        {
            return null;
        }
        ProjectNotice? notice;
        try
        {
            notice = await client.FetchAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return notice is not null && notice.IsActive(currentVersion, now) &&
               !string.Equals(notice.Id, settings.DismissedNoticeId, StringComparison.Ordinal)
            ? notice
            : null;
    }
}
