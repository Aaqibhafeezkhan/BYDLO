namespace Bydlo.Core;

/// <summary>
/// Single source of truth for the BYDLO application version (issue #143).
/// The assembly version is stamped at build time from <c>Directory.Build.props</c>;
/// the fallback is a last resort for version-less dynamic contexts and is never
/// bumped per release.
/// </summary>
public static class AppVersion
{
    public const string Fallback = "0.0.0";

    public static string Current => typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? Fallback;

    public static string UserAgent => "BYDLO/" + Current;
}
