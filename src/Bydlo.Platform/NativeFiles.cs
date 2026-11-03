namespace Bydlo.Platform;

/// <summary>Host filesystem operations, independent of the engine's target platform.</summary>
public static class NativeFiles
{
    public static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }
    }
}
