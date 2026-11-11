namespace Bydlo.Core.Diagnostics;

/// <summary>
/// GitHub locations the optional features read from (issue #438). The first-run consent screen lists
/// them so the data origin is visible in the app, not only in the documentation. Each entry is the
/// human-readable page of the file or feed the corresponding client downloads.
/// </summary>
public static class GitHubSources
{
    public const string Repository = "https://github.com/endercodezz/BYDLO";

    /// <summary>Release feed behind update checks and the release notification.</summary>
    public static string Releases => Repository + "/releases";

    /// <summary>Repository file with notification content (<c>notice.json</c>).</summary>
    public static string Notice => Repository + "/blob/main/notice.json";

    /// <summary>Repository file with the theme gallery index (<c>themes/index.json</c>).</summary>
    public static string ThemeGallery => Repository + "/blob/main/themes/index.json";
}
