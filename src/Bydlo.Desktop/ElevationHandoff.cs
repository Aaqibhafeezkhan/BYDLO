using System.Diagnostics;
using Bydlo.Core.Platform;

namespace Bydlo.Desktop;

internal static class ElevationHandoff
{
    internal static bool WaitForParent(ref string[] args, Func<int, bool>? wait = null)
    {
        var markers = args.Where(a => a.StartsWith(ElevatedRestart.ParentArgument, StringComparison.Ordinal)).ToArray();
        args = args.Except(markers).ToArray();
        if (markers.Length == 0) { return true; }
        if (markers.Length != 1 || !int.TryParse(markers[0][ElevatedRestart.ParentArgument.Length..], out var id)
            || id <= 0 || id == Environment.ProcessId)
        {
            return false;
        }

        return (wait ?? WaitForExit)(id);
    }

    private static bool WaitForExit(int id)
    {
        try
        {
            using var parent = Process.GetProcessById(id);
            // A failed cleanup leaves the parent running. Never initialize a second engine owner.
            return parent.WaitForExit(30_000);
        }
        catch (ArgumentException) { return true; } // Parent has already exited.
        catch (InvalidOperationException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
