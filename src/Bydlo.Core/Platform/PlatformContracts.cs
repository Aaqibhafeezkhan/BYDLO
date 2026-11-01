using System.Net.NetworkInformation;

namespace Bydlo.Core.Platform;

/// <summary>Where BYDLO keeps its data. Implementations resolve OS-appropriate per-user folders.</summary>
public interface IAppPaths
{
    string DataDirectory { get; }

    string LogsDirectory { get; }

    string EnginesDirectory { get; }

    string SessionsDirectory { get; }

    string ProfilesFile { get; }

    string SettingsFile { get; }

    string JournalFile { get; }
}

/// <summary>Simple implementation rooted in one folder (tests, portable mode).</summary>
public sealed class RootedAppPaths(string root) : IAppPaths
{
    public string DataDirectory { get; } = root;

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public string EnginesDirectory => Path.Combine(DataDirectory, "engines");

    public string SessionsDirectory => Path.Combine(DataDirectory, "sessions");

    public string ProfilesFile => Path.Combine(DataDirectory, "profiles.json");

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public string JournalFile => Path.Combine(DataDirectory, "process-journal.json");
}

/// <summary>Administrator / root detection and elevation. Separates user-level and privileged operations.</summary>
public interface IPrivilegeService
{
    bool IsElevated { get; }

    /// <summary>Whether the platform can relaunch BYDLO elevated (UAC on Windows).</summary>
    bool CanRestartElevated { get; }

    /// <summary>Relaunches the current executable elevated. Returns false if the user declined.</summary>
    bool TryRestartElevated(IReadOnlyList<string> args);
}

/// <summary>OS-level proxy settings. Disabled by default; guarded by <see cref="Security.NetworkSafetyGuard"/>.</summary>
public interface ISystemProxyService
{
    bool IsSupported { get; }

    Task SetAsync(string host, int port, CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}

public sealed class UnsupportedSystemProxyService : ISystemProxyService
{
    public bool IsSupported => false;

    public Task SetAsync(string host, int port, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException("Setting the system proxy is not implemented on this platform yet.");

    public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
}

public interface IPortChecker
{
    bool IsTcpPortInUse(int port);
}

/// <summary>Read-only check of listening TCP sockets. Does not open or bind anything.</summary>
public sealed class TcpPortChecker : IPortChecker
{
    public bool IsTcpPortInUse(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);
        }
        catch (NetworkInformationException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }
}
