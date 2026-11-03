using Bydlo.Core.Models;

namespace Bydlo.Platform.Processes;

internal interface IProcessExitOperations
{
    bool SignalUnix();
    bool SignalWindowsEvent(string name);
    bool CloseWindow();
}

internal static class TerminationPolicy
{
    public static bool Request(OsFamily os, string? eventName, IProcessExitOperations operations)
    {
        if (os is OsFamily.Linux or OsFamily.MacOS) { return operations.SignalUnix(); }
        if (os == OsFamily.Windows && eventName is not null)
        {
            const string prefix = "Local\\BYDLO-OpenVPN-";
            if (!eventName.StartsWith(prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(eventName[prefix.Length..], "N", out _)) { return false; }
            return operations.SignalWindowsEvent(eventName);
        }
        return operations.CloseWindow();
    }
}
