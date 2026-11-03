using Bydlo.Core.Models;

namespace Bydlo.Core.Platform;

/// <summary>Opt-in elevation at startup (issue #246). Pure decision logic, no process start.</summary>
public static class StartupElevation
{
    public static bool ShouldElevateOnStartup(
        AppSettings settings,
        IPrivilegeService privileges,
        bool isMock,
        bool isLocked)
    {
        if (isMock || isLocked)
        {
            return false;
        }

        if (!settings.RunElevatedOnStartup)
        {
            return false;
        }

        if (privileges.IsElevated || !privileges.CanRestartElevated)
        {
            return false;
        }

        return true;
    }
}
