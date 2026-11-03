using Bydlo.Core.Processes;
using System.Diagnostics;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;

namespace Bydlo.Platform;

/// <summary>Resolves per-user data folders. <c>BYDLO_DATA_DIR</c> overrides; a <c>portable.txt</c> next to the app enables portable mode.</summary>
public static class AppPathsFactory
{
    public const string DataDirVariable = "BYDLO_DATA_DIR";

    public static IAppPaths Create()
    {
        var overrideDir = Environment.GetEnvironmentVariable(DataDirVariable);
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return Ensure(new RootedAppPaths(Path.GetFullPath(overrideDir)));
        }

        var baseDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, "portable.txt")))
        {
            return Ensure(new RootedAppPaths(Path.Combine(baseDir, "data")));
        }

        string root;
        if (OperatingSystem.IsWindows())
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BYDLO");
        }
        else if (OperatingSystem.IsMacOS())
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "BYDLO");
        }
        else if (OperatingSystem.IsAndroid())
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "bydlo");
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            root = Path.Combine(string.IsNullOrEmpty(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
                : xdg, "bydlo");
        }

        return Ensure(new RootedAppPaths(root));
    }

    private static RootedAppPaths Ensure(RootedAppPaths paths)
    {
        foreach (var d in new[] { paths.DataDirectory, paths.LogsDirectory, paths.EnginesDirectory, paths.SessionsDirectory })
        {
            Directory.CreateDirectory(d);
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(paths.DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (IOException)
            {
            }
        }

        return paths;
    }
}

/// <summary>Administrator / root detection. Privileged work is isolated behind this service and the engines that need it.</summary>
public sealed class PrivilegeService : IPrivilegeService
{
    public bool IsElevated => Environment.IsPrivilegedProcess;

    public bool CanRestartElevated => OperatingSystem.IsWindows();

    public bool TryRestartElevated(IReadOnlyList<string> args)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe)
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var child = Process.Start(psi);
            if (child is null) { return false; }
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User cancelled the UAC prompt.
            return false;
        }
    }
}

public static class SecretStoreFactory
{
    // Selection is pure: tests provide factories that never access native vaults.
    internal static ISecretStore Select(Bydlo.Core.Models.PlatformInfo platform,
        Func<ISecretStore> windows, Func<ISecretStore> portable) =>
        platform.Os == Bydlo.Core.Models.OsFamily.Windows ? windows() : portable();

    public static ISecretStore Create(IAppPaths paths, bool isolated = false)
    {
        var directory = Path.Combine(paths.DataDirectory, "secrets");
        return Select(Bydlo.Core.Models.PlatformInfo.Current, () => new DpapiSecretStore(directory),
            () => CreatePortable(paths, isolated || NetworkSafetyGuard.IsLocked, scope =>
            OperatingSystem.IsMacOS() ? new MacKeychainBackend(scope)
                : OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid() ? new LinuxSecretBackend(scope) : null));
    }

    // Backend creation is deferred so isolated runs never access the user's system vault.
    internal static ISecretStore CreatePortable(IAppPaths paths, bool isolated,
        Func<string, ISystemSecretBackend?> createBackend)
    {
        var fallback = new AesFileSecretStore(Path.Combine(paths.DataDirectory, "secrets"));
        if (isolated)
        {
            return fallback;
        }

        var scope = "BYDLO-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(paths.DataDirectory))));
        var backend = createBackend(scope);
        return backend is null ? fallback : new MigratingSecretStore(backend, fallback);
    }

    public static bool IsUsingSystemFallback(ISecretStore store) => store is MigratingSecretStore { UsingFallback: true };
}

public static class AmneziaNetworkFactory
{
    public static IAmneziaNetwork Create(IProcessRunner runner, IAppPaths paths) =>
        OperatingSystem.IsMacOS() ? new MacAmneziaNetwork(runner, paths.DataDirectory)
            : new AmneziaNetwork(runner, Bydlo.Core.Models.PlatformInfo.Current.Os, paths.DataDirectory);
}

public static class SystemProxyFactory
{
    public static ISystemProxyService Create(IProcessRunner runner, IAppPaths paths)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsSystemProxyService(paths.DataDirectory);
        }
        if (OperatingSystem.IsMacOS())
        {
            return new JournaledSystemProxyService(runner, DesktopProxyKind.MacOs, paths.DataDirectory);
        }
        if (OperatingSystem.IsLinux())
        {
            var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
            if (desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase) || desktop.Contains("Plasma", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var version in new[] { "6", "5" })
                {
                    if (HasTool("kreadconfig" + version) && HasTool("kwriteconfig" + version))
                    {
                        return new JournaledSystemProxyService(runner,
                            version == "6" ? DesktopProxyKind.Kde6 : DesktopProxyKind.Kde5, paths.DataDirectory);
                    }
                }
            }
            if (desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase) && HasTool("gsettings"))
            {
                return new JournaledSystemProxyService(runner, DesktopProxyKind.Gnome, paths.DataDirectory);
            }
        }
        return new UnsupportedSystemProxyService();
    }

    private static bool HasTool(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(path => Path.IsPathFullyQualified(path) && File.Exists(Path.Combine(path, name)));
}
