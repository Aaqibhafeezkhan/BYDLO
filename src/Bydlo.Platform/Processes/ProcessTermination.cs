using System.Diagnostics;
using System.Runtime.InteropServices;
using Bydlo.Core.Models;

namespace Bydlo.Platform.Processes;

/// <summary>Give session-scoped route owners a chance to remove their OS resources before exit.</summary>
internal static partial class ProcessTermination
{
    public static bool RequestGracefulExit(Process process, string? windowsExitEventName = null) =>
        TerminationPolicy.Request(PlatformInfo.Current.Os, windowsExitEventName, new NativeExitOperations(process));

    private sealed class NativeExitOperations(Process process) : IProcessExitOperations
    {
        public bool SignalUnix() => SendSignal(process.Id, 15) == 0;
        public bool CloseWindow() => process.CloseMainWindow();
        public bool SignalWindowsEvent(string name)
        {
            if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
            try { using var exitEvent = EventWaitHandle.OpenExisting(name); return exitEvent.Set(); }
            catch (WaitHandleCannotBeOpenedException) { return false; }
        }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SendSignal(int pid, int signal);
}
