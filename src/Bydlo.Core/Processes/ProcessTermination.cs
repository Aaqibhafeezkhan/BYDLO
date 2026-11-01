using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bydlo.Core.Processes;

/// <summary>Give session-scoped route owners a chance to remove their OS resources before exit.</summary>
internal static partial class ProcessTermination
{
    public static bool RequestGracefulExit(Process process)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return SendSignal(process.Id, 15) == 0; // SIGTERM is handled by sing-box's shutdown path.
        }

        return process.CloseMainWindow();
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SendSignal(int pid, int signal);
}
