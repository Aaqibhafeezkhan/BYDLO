using Bydlo.Core.Models;

namespace Bydlo.Core.Engines;

public enum EngineState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Failed,
}

/// <summary>Whether an engine can be used right now on this machine.</summary>
public enum AvailabilityStatus
{
    Ready,

    /// <summary>Supported, but the binary / runtime is not installed yet.</summary>
    NotInstalled,

    /// <summary>Supported, but needs administrator / root rights.</summary>
    NeedsElevation,

    /// <summary>Planned for this platform but not implemented in this version.</summary>
    NotImplemented,

    /// <summary>Cannot work on this platform at all (e.g. WinDivert outside Windows).</summary>
    Unsupported,
}

public sealed record EngineAvailability(AvailabilityStatus Status, string Message, string? ExecutablePath = null)
{
    public string? LocalizationKey { get; init; }

    public bool IsReady => Status == AvailabilityStatus.Ready;

    public static EngineAvailability Ready(string path) => new(AvailabilityStatus.Ready, "Ready", path);

    public static EngineAvailability NotImplemented(string message, string? localizationKey = null) =>
        new(AvailabilityStatus.NotImplemented, message) { LocalizationKey = localizationKey };

    public static EngineAvailability Unsupported(string message, string? localizationKey = null) =>
        new(AvailabilityStatus.Unsupported, message) { LocalizationKey = localizationKey };
}

/// <summary>Per-platform implementation state; used by the UI and by the compatibility matrix.</summary>
public enum SupportLevel
{
    Implemented,
    Planned,
    Unsupported,
}

public sealed record EngineDescriptor
{
    public required EngineKind Kind { get; init; }

    public required string DisplayName { get; init; }

    public required string Upstream { get; init; }

    public required string License { get; init; }

    public IReadOnlySet<ProtocolKind> Protocols { get; init; } = new HashSet<ProtocolKind>();

    public IReadOnlyDictionary<OsFamily, SupportLevel> Platforms { get; init; } = new Dictionary<OsFamily, SupportLevel>();

    /// <summary>True for standalone tools (Zapret, TgWsProxy) that run alongside the primary tunnel.</summary>
    public bool IsTool { get; init; }

    public SupportLevel SupportOn(OsFamily os) => Platforms.TryGetValue(os, out var s) ? s : SupportLevel.Unsupported;
}

public sealed record TrafficSample(DateTimeOffset Time, long UploadBytesPerSecond, long DownloadBytesPerSecond, long TotalUpload, long TotalDownload);

/// <summary>Credentials for LAN clients of the local proxy inbound. Never persisted in settings JSON.</summary>
public sealed record LocalProxyAuth(string Username, string Password);

public sealed record EngineStartRequest
{
    public ServerGroup? Group { get; init; }
    public IReadOnlyList<ChainHop>? GroupMembers { get; init; }
    public Profile? Profile { get; init; }

    public ProfileSecrets Secrets { get; init; } = ProfileSecrets.Empty;

    /// <summary>LAN proxy credentials, set only when <see cref="AppSettings.AllowLan"/> is on.</summary>
    public LocalProxyAuth? LocalProxyAuth { get; init; }

    /// <summary>Relay chain hops (entry first, exit last). <c>null</c> for a direct connection.</summary>
    public IReadOnlyList<ChainHop>? Chain { get; init; }

    public required AppSettings Settings { get; init; }

    /// <summary>Per-session working directory for generated configs (inside BYDLO's data dir).</summary>
    public required string WorkDirectory { get; init; }
    public string? RuleSetDirectory { get; init; }
}

public sealed class EngineStateChangedEventArgs(EngineState state, string? error = null) : EventArgs
{
    public EngineState State { get; } = state;

    public string? Error { get; } = error;
}

/// <summary>A network engine adapter (one running instance).</summary>
public interface IEngine : IAsyncDisposable
{
    EngineDescriptor Descriptor { get; }

    EngineState State { get; }

    /// <summary>Raised from background threads.</summary>
    event EventHandler<EngineStateChangedEventArgs>? StateChanged;

    /// <summary>Raised from background threads with redacted lines.</summary>
    event EventHandler<string>? Log;

    /// <summary>Raised roughly once per second while running, when the engine can report traffic.</summary>
    event EventHandler<TrafficSample>? Traffic;

    bool CanHandle(Profile profile);

    Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default);

    Task StartAsync(EngineStartRequest request, CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);
}

public sealed class EngineException(string message, Exception? inner = null, string? localizationKey = null,
    string[]? localizationArguments = null) : Exception(message, inner)
{
    public string? LocalizationKey { get; } = localizationKey;
    public string[] LocalizationArguments { get; } = localizationArguments ?? [];
}

/// <summary>Restarts transport without releasing a retained TUN.</summary>
public interface IReconnectableEngine
{
    Task ReconnectAsync(EngineStartRequest request, CancellationToken ct = default);
}

/// <summary>Null means the core has not confirmed a selected member.</summary>
public interface IServerGroupSource
{
    Guid? SelectedGroupProfileId { get; }
}
