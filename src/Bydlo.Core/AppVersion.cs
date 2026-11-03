namespace Bydlo.Core;

/// <summary>
/// Single source of truth for the BYDLO application version (issue #143).
/// Assembly version comes from <c>Directory.Build.props</c>; the fallback
/// must be bumped together with it on every release.
/// </summary>
public static class AppVersion
{
    public const string Fallback = "0.2.0";

    public static string Current => typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? Fallback;

    public static string UserAgent => "BYDLO/" + Current;
}
