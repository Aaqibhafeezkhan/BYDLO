using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Bydlo.Core.Security;
using Bydlo.Platform;

namespace Bydlo.Helper;

[UnsupportedOSPlatform("windows")]
internal static class UnixHelperInstaller
{
    private const string Label = "online.bydlo.helper";
    private static string Definition => OperatingSystem.IsMacOS()
        ? Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, "Library", "LaunchDaemons", Label + ".plist")
        : Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, "etc", "systemd", "system", "bydlo-helper.service");

    public static void Install(string owner)
    {
        NetworkSafetyGuard.EnsureAllowed("install the privileged helper");
        if (!uint.TryParse(owner, out var uid) || uid == 0) { throw new InvalidDataException("A non-root UID is required."); }
        var target = HelperPaths.InstallDirectory;
        UnixHelperSecurity.EnsureProtectedParents(target);
        if (Directory.Exists(target) || File.Exists(Definition)) { throw new IOException("Remove the existing helper first."); }
        var engines = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "engines"));
        if (!Directory.Exists(engines)) { throw new IOException("Bundled engines are missing."); }
        CopyTree(AppContext.BaseDirectory, target);
        CopyTree(engines, Path.Combine(target, "engines"));
        File.WriteAllText(Path.Combine(target, "owner.sid"), uid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var hashes = Directory.GetFiles(Path.Combine(target, "engines"), "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(target, file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        File.WriteAllText(Path.Combine(target, "bundle.sha256.json"), JsonSerializer.Serialize(hashes, HelperJsonContext.Default.DictionaryStringString));
        var data = Path.Combine(target, "data");
        Directory.CreateDirectory(data);
        File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        HelperServer.VerifyBundle();
        HelperManagement.WriteInstalledBuild(HelperManagement.GetBuild(Bydlo.Engines.Installation.EngineLock.BundleVersion));
        Directory.CreateDirectory(Path.GetDirectoryName(Definition)!);
        File.WriteAllText(Definition, OperatingSystem.IsMacOS() ? HelperServiceDefinitions.BuildLaunchDaemon(HelperPaths.Executable) : HelperServiceDefinitions.BuildSystemdUnit(HelperPaths.Executable));
        File.SetUnixFileMode(Definition, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        if (OperatingSystem.IsMacOS()) { Run("/bin/launchctl", "bootstrap", "system", Definition); }
        else { Run(Systemctl(), "daemon-reload"); Run(Systemctl(), "enable", "--now", "bydlo-helper.service"); }
    }

    private static void CopyTree(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) { throw new InvalidDataException("Linked installation paths are forbidden."); }
        Directory.CreateDirectory(target);
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) { throw new InvalidDataException("Linked installation files are forbidden."); }
            var destination = Path.Combine(target, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0) { CopyTree(entry, destination); }
            else
            {
                File.Copy(entry, destination, false);
                var executable = (File.GetUnixFileMode(entry) & UnixFileMode.UserExecute) != 0;
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead |
                    (executable ? UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute : 0));
            }
        }
    }

    public static void Remove()
    {
        NetworkSafetyGuard.EnsureAllowed("remove the privileged helper");
        if (File.Exists(Definition))
        {
            if (OperatingSystem.IsMacOS())
            {
                // A failed bootstrap can leave only the plist. Never mistake a failed stop for an unloaded job.
                var loaded = ExitCode("/bin/launchctl", "print", "system/" + Label);
                if (loaded == 0) { Run("/bin/launchctl", "bootout", "system/" + Label); }
                else if (loaded != 113) { throw new IOException("Cannot determine launchd job state."); }
            }
            else { Run(Systemctl(), "disable", "--now", "bydlo-helper.service"); }
        }
        File.Delete(Definition);
        if (!OperatingSystem.IsMacOS()) { Run(Systemctl(), "daemon-reload"); }
        var root = Path.GetFullPath(HelperPaths.InstallDirectory);
        UnixHelperSecurity.EnsureProtectedParents(root);
        var expected = OperatingSystem.IsMacOS()
            ? Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, "Library", "PrivilegedHelperTools", "BYDLO")
            : Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, "usr", "local", "lib", "bydlo-helper");
        if (!HelperServiceDefinitions.IsSafeToDeleteRoot(root, expected, AppContext.BaseDirectory, StringComparison.Ordinal))
        { throw new IOException("Remove the helper using the original release folder."); }
        if (Directory.Exists(root)) { Directory.Delete(root, true); }
    }

    private static string Systemctl() => File.Exists("/usr/bin/systemctl") ? "/usr/bin/systemctl" : "/bin/systemctl";
    private static void Run(string exe, params string[] args)
    {
        if (ExitCode(exe, args) != 0) { throw new IOException("The service manager rejected the operation."); }
    }

    private static int ExitCode(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in args) { info.ArgumentList.Add(arg); }
        using var process = Process.Start(info) ?? throw new IOException("Could not start the service manager.");
        process.WaitForExit();
        return process.ExitCode;
    }
}
