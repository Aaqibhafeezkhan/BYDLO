using System.Text.Json;
using Bydlo.Core.Security;

namespace Bydlo.Core.Diagnostics;

/// <summary>GitHub release info used by the update check (issue #19).</summary>
public sealed record GitHubRelease(string Tag, string HtmlUrl);

/// <summary>
/// Checks for BYDLO and engine updates via the GitHub Releases API.
/// Network access happens only on explicit user action or scheduled check;
/// tests inject a fake <see cref="HttpMessageHandler"/> and never touch the network.
/// </summary>
public sealed class GitHubUpdateClient(Func<HttpMessageHandler>? handlerFactory = null)
{
    public const string BydloReleasesUrl = "https://api.github.com/repos/endercodezz/BYDLO/releases/latest";

    /// <summary>Compares a pinned/current version with a release tag. Tags may carry a leading "v".</summary>
    public static bool IsNewer(string current, string tag)
    {
        var currentParts = NumericParts(current);
        var tagParts = NumericParts(tag);
        var length = Math.Max(currentParts.Length, tagParts.Length);
        for (var i = 0; i < length; i++)
        {
            var left = i < currentParts.Length ? currentParts[i] : 0;
            var right = i < tagParts.Length ? tagParts[i] : 0;
            if (right != left)
            {
                return right > left;
            }
        }
        return false;
    }

    /// <summary>Parses a <c>releases/latest</c> response. Public for isolated parser tests.</summary>
    public static GitHubRelease? ParseRelease(string json)
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
            if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            var tagName = tag.GetString();
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return null;
            }
            var url = root.TryGetProperty("html_url", out var link) && link.ValueKind == JsonValueKind.String
                ? link.GetString() ?? ""
                : "";
            return new GitHubRelease(tagName, url);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Owner/repo extracted from an upstream URL, for engine checks. Null when not a GitHub URL.</summary>
    public static (string Owner, string Repo)? ParseGitHubRepo(string upstream)
    {
        if (!Uri.TryCreate(upstream.Trim(), UriKind.Absolute, out var uri) || uri.Host != "github.com")
        {
            return null;
        }
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? (segments[0], segments[1]) : null;
    }

    public async Task<GitHubRelease?> CheckBydloAsync(string currentVersion, CancellationToken ct = default)
    {
        if (handlerFactory is null)
        {
            NetworkSafetyGuard.EnsureAllowed("check for updates");
        }
        using var http = CreateClient();
        using var response = await http.GetAsync(BydloReleasesUrl, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var release = ParseRelease(body);
        return release is not null && IsNewer(currentVersion, release.Tag) ? release : null;
    }

    public async Task<GitHubRelease?> CheckEngineAsync(string upstream, string pinnedVersion, CancellationToken ct = default)
    {
        var repo = ParseGitHubRepo(upstream);
        if (repo is null)
        {
            return null;
        }
        if (handlerFactory is null)
        {
            NetworkSafetyGuard.EnsureAllowed("check for updates");
        }
        var url = $"https://api.github.com/repos/{repo.Value.Owner}/{repo.Value.Repo}/releases/latest";
        using var http = CreateClient();
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var release = ParseRelease(body);
        return release is not null && IsNewer(pinnedVersion, release.Tag) ? release : null;
    }

    private HttpClient CreateClient()
    {
        var handler = handlerFactory?.Invoke() ?? new SocketsHttpHandler();
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BYDLO/0.1");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static int[] NumericParts(string version)
    {
        var core = version.Trim().TrimStart('v', 'V');
        var end = core.IndexOfAny(['-', '+']);
        if (end >= 0)
        {
            core = core[..end];
        }
        return core.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out var n) && n >= 0 ? n : 0)
            .ToArray();
    }
}
