namespace Bydlo.Core.Platform;

/// <summary>Ask for elevation before touching active sessions. The child waits for this process to exit.</summary>
public static class ElevatedRestart
{
    public const string ParentArgument = "--elevation-parent=";

    public static async Task<bool> TryAsync(IPrivilegeService privileges, IReadOnlyList<string> args,
        int parentId, Func<Task> stopSessions)
    {
        if (privileges.IsElevated || !privileges.CanRestartElevated)
        {
            return false;
        }

        var launchArgs = args.Where(a => !a.StartsWith(ParentArgument, StringComparison.Ordinal)).ToList();
        launchArgs.Add(ParentArgument + parentId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!privileges.TryRestartElevated(launchArgs))
        {
            return false;
        }

        await stopSessions();
        return true;
    }
}
