using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Bydlo.Core.Models;
using Bydlo.Core.Security;

namespace Bydlo.Core.Diagnostics;

/// <summary>A human contributor as reported by GitHub. Profile links are built from the login, never from the response.</summary>
public sealed record Contributor(string Login, int Contributions)
{
    [JsonIgnore]
    public string ProfileUrl => "https://github.com/" + Uri.EscapeDataString(Login);
}

/// <summary>
/// Reads the repository contributor list from the GitHub API (issue #168), so the UI never hardcodes names.
/// Tests inject a fake <see cref="HttpMessageHandler"/> and never touch the network.
/// </summary>
public sealed partial class GitHubContributorsClient(Func<HttpMessageHandler>? handlerFactory = null)
{
    public const string ContributorsUrl = "https://api.github.com/repos/endercodezz/BYDLO/contributors?per_page=100";
    public const string ContributorsPageUrl = "https://github.com/endercodezz/BYDLO/graphs/contributors";

    /// <summary>Parses a <c>contributors</c> response: users only (no bots), valid logins, most commits first.</summary>
    public static IReadOnlyList<Contributor> ParseContributors(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            var result = new Dictionary<string, Contributor>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                    !string.Equals(type.GetString(), "User", StringComparison.Ordinal) ||
                    !item.TryGetProperty("login", out var loginElement) || loginElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var login = loginElement.GetString() ?? "";
                if (!IsValidLogin(login))
                {
                    continue;
                }
                var contributions = item.TryGetProperty("contributions", out var count) && count.TryGetInt32(out var n) && n > 0 ? n : 0;
                if (!result.TryGetValue(login, out var existing) || existing.Contributions < contributions)
                {
                    result[login] = new Contributor(login, contributions);
                }
            }
            return result.Values
                .OrderByDescending(c => c.Contributions)
                .ThenBy(c => c.Login, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>GitHub login rules: 1-39 alphanumerics or single hyphens, not starting or ending with a hyphen.</summary>
    public static bool IsValidLogin(string login) => LoginPattern().IsMatch(login);

    public async Task<IReadOnlyList<Contributor>> FetchAsync(CancellationToken ct = default)
    {
        if (handlerFactory is null)
        {
            NetworkSafetyGuard.EnsureAllowed("load contributors");
        }
        using var http = new HttpClient(handlerFactory?.Invoke() ?? new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(AppVersion.UserAgent);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.GetAsync(ContributorsUrl, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseContributors(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    [GeneratedRegex("^(?=.{1,39}$)[A-Za-z0-9](?:-?[A-Za-z0-9])*$", RegexOptions.CultureInvariant)]
    private static partial Regex LoginPattern();
}

/// <summary>
/// Refresh policy for the cached contributor list: only with the automatic-updates consent on, never
/// while the network is blocked (Mock Mode, development lock), at most once per 24 hours. Failures
/// keep the cache.
/// </summary>
public sealed class ContributorsRefresher(GitHubContributorsClient client, TimeProvider? timeProvider = null)
{
    public async Task<IReadOnlyList<Contributor>> RefreshAsync(AppSettings settings, bool networkBlocked, CancellationToken ct = default)
    {
        if (!FeatureConsent.From(settings).Allows(OptionalFeature.AutomaticUpdates) ||
            !settings.CheckForUpdates || networkBlocked)
        {
            return settings.Contributors;
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (settings.LastContributorsCheck is { } last && now - last < TimeSpan.FromHours(24))
        {
            return settings.Contributors;
        }
        // Failed attempts also respect the interval, avoiding a request on every offline startup.
        settings.LastContributorsCheck = now;
        try
        {
            var fresh = await client.FetchAsync(ct).ConfigureAwait(false);
            if (fresh.Count > 0)
            {
                settings.Contributors = fresh.ToList();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            // The sidebar keeps showing the cached list.
        }
        return settings.Contributors;
    }
}
