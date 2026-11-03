namespace Bydlo.Core.Platform;

/// <summary>Ask for elevation before touching active sessions. The child waits for this process to exit.</summary>
public static class ElevatedRestart
{
    public const string ParentArgument = "--elevation-parent=";
    public const string DataDirectoryArgument = "--data-dir=";

    public static IReadOnlyList<string> BuildArguments(IReadOnlyList<string> args, int parentId,
        bool forcedMock, string dataDirectory)
    {
        var launchArgs = args.Where(a => !a.StartsWith(ParentArgument, StringComparison.Ordinal)
            && !a.StartsWith(DataDirectoryArgument, StringComparison.Ordinal)).ToList();
        if (forcedMock && !launchArgs.Contains("--mock", StringComparer.Ordinal))
        {
            launchArgs.Add("--mock");
        }
        launchArgs.Add(DataDirectoryArgument + Path.GetFullPath(dataDirectory));
        launchArgs.Add(ParentArgument + parentId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return launchArgs;
    }

    public static async Task<bool> TryAsync(IPrivilegeService privileges, IReadOnlyList<string> args,
        int parentId, Func<Task> stopSessions, bool forcedMock, string dataDirectory)
    {
        if (privileges.IsElevated || !privileges.CanRestartElevated)
        {
            return false;
        }

        var launchArgs = BuildArguments(args, parentId, forcedMock, dataDirectory);
        if (!privileges.TryRestartElevated(launchArgs))
        {
            return false;
        }

        await stopSessions();
        return true;
    }
}
