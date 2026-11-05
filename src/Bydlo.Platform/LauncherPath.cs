namespace Bydlo.Platform;

/// <summary>
/// Path that desktop entries (autostart, import associations) should launch. Linux packages may run the
/// app through a wrapper (Nix sets <c>BYDLO_LAUNCHER</c>) or from a transient AppImage mount (<c>APPIMAGE</c>);
/// registering <see cref="Environment.ProcessPath"/> there would point at a binary that cannot start on its own.
/// </summary>
public static class LauncherPath
{
    public const string LauncherVariable = "BYDLO_LAUNCHER";
    public const string AppImageVariable = "APPIMAGE";

    public static string? Current => Resolve(OperatingSystem.IsLinux(), Environment.GetEnvironmentVariable, Environment.ProcessPath, File.Exists);

    internal static string? Resolve(bool linux, Func<string, string?> environment, string? processPath, Func<string, bool> exists)
    {
        if (linux)
        {
            foreach (var variable in new[] { LauncherVariable, AppImageVariable })
            {
                var candidate = environment(variable);
                if (!string.IsNullOrEmpty(candidate) && candidate.StartsWith('/') && !candidate.Any(char.IsControl) && exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return processPath;
    }
}
