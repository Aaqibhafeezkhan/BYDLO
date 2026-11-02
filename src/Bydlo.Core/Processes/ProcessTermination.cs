using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bydlo.Core.Processes;

/// <summary>Give session-scoped route owners a chance to remove their OS resources before exit.</summary>
internal static partial class ProcessTermination
{
    public static bool RequestGracefulExit(Process process, string? windowsExitEventName = null)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return SendSignal(process.Id, 15) == 0; // SIGTERM is handled by sing-box's shutdown path.
        }

        if (OperatingSystem.IsWindows() && windowsExitEventName is not null)
        {
            // Only BYDLO-generated names are accepted from the recovery journal.
            const string prefix = "Local\\BYDLO-OpenVPN-";
            if (!windowsExitEventName.StartsWith(prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(windowsExitEventName[prefix.Length..], "N", out _)) { return false; }
            try { using var exitEvent = EventWaitHandle.OpenExisting(windowsExitEventName); return exitEvent.Set(); }
            catch (WaitHandleCannotBeOpenedException) { return false; }
        }
        return process.CloseMainWindow();
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SendSignal(int pid, int signal);
}
