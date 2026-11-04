namespace Bydlo.Platform;

public static class HelperPaths
{
    public static string InstallDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BYDLO Helper")
        : OperatingSystem.IsMacOS() ? Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, "Library", "PrivilegedHelperTools", "BYDLO")
        : Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, "usr", "local", "lib", "bydlo-helper");
    // The system run directory on macOS can be group-writable.
    public static string SocketDirectory => OperatingSystem.IsMacOS()
        ? Path.Combine(InstallDirectory, "run")
        : Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, "var", "run", "bydlo-helper");
    public static string SocketPath => Path.Combine(SocketDirectory, "control.sock");
    public static string Executable => Path.Combine(InstallDirectory, OperatingSystem.IsWindows() ? "Bydlo.Helper.exe" : "Bydlo.Helper");
}
