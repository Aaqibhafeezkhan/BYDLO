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

            Process.Start(psi)?.Dispose();
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
    public static ISecretStore Create(IAppPaths paths) =>
        OperatingSystem.IsWindows()
            ? new DpapiSecretStore(Path.Combine(paths.DataDirectory, "secrets"))
            : new AesFileSecretStore(Path.Combine(paths.DataDirectory, "secrets"));
}

public static class SystemProxyFactory
{
    public static ISystemProxyService Create() =>
        OperatingSystem.IsWindows() ? new WindowsSystemProxyService() : new UnsupportedSystemProxyService();
}
