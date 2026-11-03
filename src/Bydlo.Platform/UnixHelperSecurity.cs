using System.Diagnostics;
using System.Runtime.Versioning;

namespace Bydlo.Platform;

[UnsupportedOSPlatform("windows")]
public static class UnixHelperSecurity
{
    /// <summary>A user must not be able to replace any ancestor of privileged code or the socket directory.</summary>
    public static void EnsureProtectedParents(string directory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(directory));
        while (current is not null)
        {
            if (current.Exists)
            {
                var mode = File.GetUnixFileMode(current.FullName);
                if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                { throw new UnauthorizedAccessException("The helper requires root-owned, non-writable parent directories."); }
                var info = new ProcessStartInfo("/usr/bin/stat")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                info.ArgumentList.Add(OperatingSystem.IsMacOS() ? "-f" : "-c");
                info.ArgumentList.Add("%u");
                info.ArgumentList.Add(current.FullName);
                using var process = Process.Start(info) ?? throw new IOException("Cannot verify directory ownership.");
                var owner = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (process.ExitCode != 0 || owner != "0")
                { throw new UnauthorizedAccessException("The helper requires root-owned parent directories."); }
            }
            current = current.Parent;
        }
    }
}
